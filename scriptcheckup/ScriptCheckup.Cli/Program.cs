using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Sarif = Microsoft.CodeAnalysis.Sarif;
using Microsoft.CodeAnalysis.Sarif;
using ScriptCheckup.Analyzers;
using ScriptCheckup.Analyzers.FlowAnalysis;

namespace ScriptCheckup.Cli
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
            {
                PrintHelp();
                return 0;
            }

            var files = new List<string>();
            var references = new List<string>();
            string? sarifOut = null;
            string? rulesetPath = null;
            var enabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var disabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool recursive = false;
            string? dir = null;
            var excludes = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--sarif":
                    case "-s":
                        if (i + 1 < args.Length) sarifOut = args[++i];
                        break;
                    case "--ruleset":
                    case "-r":
                        if (i + 1 < args.Length) rulesetPath = args[++i];
                        break;
                    case "--reference":
                    case "--ref":
                        if (i + 1 < args.Length) references.Add(args[++i]);
                        break;
                    case "--enable":
                        if (i + 1 < args.Length)
                            foreach (var r in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                                enabledRules.Add(NormRuleId(r));
                        break;
                    case "--disable":
                        if (i + 1 < args.Length)
                            foreach (var r in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                                disabledRules.Add(NormRuleId(r));
                        break;
                    case "--exclude":
                        if (i + 1 < args.Length)
                            foreach (var e in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries))
                                excludes.Add(e.Trim());
                        break;
                    case "--recursive":
                    case "-R":
                        recursive = true;
                        break;
                    case "--dir":
                    case "-d":
                        if (i + 1 < args.Length) dir = args[++i];
                        break;
                    default:
                        if (args[i].EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && File.Exists(args[i]))
                            files.Add(Path.GetFullPath(args[i]));
                        else if (Directory.Exists(args[i]))
                            dir = args[i];
                        break;
                }
            }

            if (dir != null)
            {
                var search = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                files.AddRange(Directory.GetFiles(dir, "*.cs", search)
                    .Select(Path.GetFullPath)
                    .Where(f => !IsExcludedPath(f, excludes)));
            }

            files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (files.Count == 0)
            {
                // Fail-closed (round 4, 2.1д): zero files analyzed is an infrastructure
                // failure, not "no warnings" — exit 3, never 1.
                Console.Error.WriteLine("No .cs files found — nothing was analyzed.");
                return 3;
            }

            if (rulesetPath != null && File.Exists(rulesetPath))
            {
                LoadRuleset(rulesetPath, enabledRules, disabledRules);
            }

            Console.WriteLine($"ScriptCheckup · Roslyn engine · analyzing {files.Count} file(s)…");

            var (diagnostics, parseErrors) = await AnalyzeFilesAsync(files, enabledRules, disabledRules, references);
            // Parse errors are blocking gate results (UE000), merged with analyzer
            // diagnostics for printing, counting and SARIF.
            diagnostics = diagnostics.AddRange(parseErrors);

            var byFile = diagnostics.GroupBy(d => d.Location.SourceTree?.FilePath ?? "<unknown>");
            int errors = 0, warnings = 0, infos = 0, analyzerFailures = 0;
            foreach (var g in byFile.OrderBy(x => x.Key))
            {
                Console.WriteLine();
                Console.WriteLine($"── {Path.GetFileName(g.Key)} ──");
                foreach (var d in g.OrderBy(x => x.Location.GetLineSpan().StartLinePosition.Line))
                {
                    var line = d.Location.GetLineSpan().StartLinePosition.Line + 1;
                    // Analyzer failures (AD*) are fail-closed: they print and count as
                    // errors even though Roslyn reports them as warnings (round 4, 2.1в).
                    bool isAnalyzerFailure = IsAnalyzerFailure(d.Id);
                    var sev = (d.Severity == DiagnosticSeverity.Error || isAnalyzerFailure)
                        ? "error"
                        : d.Severity == DiagnosticSeverity.Warning ? "warning" : "info";
                    if (isAnalyzerFailure) analyzerFailures++;
                    else if (d.Severity == DiagnosticSeverity.Error) errors++;
                    else if (d.Severity == DiagnosticSeverity.Warning) warnings++;
                    else infos++;

                    Console.WriteLine($"  {sev} {d.Id} L{line}: {d.GetMessage()}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"Summary: {errors} error(s), {warnings} warning(s), {infos} info(s)" +
                (analyzerFailures > 0 ? $", {analyzerFailures} analyzer failure(s)" : ""));

            if (sarifOut != null)
            {
                WriteSarif(diagnostics, files, sarifOut);
                Console.WriteLine($"SARIF written to {sarifOut}");
            }

            // Exit codes (round 4, 2.1г): 3 = infrastructure failure (analyzer crash),
            // distinct from 2 = rule violations. A gate must not confuse policy with
            // broken tooling.
            if (analyzerFailures > 0) return 3;
            return errors > 0 ? 2 : (warnings > 0 ? 1 : 0);
        }

        static void PrintHelp()
        {
            Console.WriteLine(@"
ScriptCheckup — Unity C# static analyzer (Roslyn engine)

Usage:
  scriptcheckup [options] <file.cs> [<file2.cs> ...]
  scriptcheckup [options] --dir <path> [--recursive]

Options:
  -s, --sarif <path>     Write results in SARIF 2.1 format
  -r, --ruleset <path>   Custom rule set (JSON or one-rule-per-line)
  --reference <dll>      Add a metadata reference (e.g. UnityEngine.dll from your
                         Unity install: Editor/Data/Managed/UnityEngine.dll).
                         Improves symbol resolution; repeatable.
  --enable <ids>         Comma-separated rule IDs to force-enable
  --disable <ids>        Comma-separated rule IDs to disable (dash-insensitive: RB-003 == RB003)
  --exclude <patterns>   Comma-separated path substrings to skip
                         (e.g. --exclude Editor,Plugins,Packages)
  -d, --dir <path>       Analyze all .cs files in directory
  -R, --recursive        Recurse into subdirectories
  -h, --help             Show this help

Exit codes:
  0  clean — no findings
  1  warnings only
  2  rule violations at error severity (incl. UE000 syntax errors)
  3  infrastructure failure — analyzer crash (AD*), zero files analyzed,
     or unreadable input. Never filtered by --disable.

Examples:
  scriptcheckup Assets/Scripts/Player.cs --sarif results.sarif
  scriptcheckup --dir Assets --recursive --disable RB-003,UL-008
  scriptcheckup --dir . -R --ruleset myrules.json --sarif out.sarif
  scriptcheckup --dir Assets -R --reference /opt/unity/Editor/Data/Managed/UnityEngine.dll
");
        }

        /// <summary>Rule IDs are compared dash-insensitively: RB-003 == RB003.</summary>
        static string NormRuleId(string id) => id.Trim().Replace("-", "");

        /// <summary>
        /// Default exclusions (build output) plus user --exclude substrings.
        /// Use e.g. --exclude Editor,Plugins,Packages to skip Unity special folders.
        /// </summary>
        static bool IsExcludedPath(string fullPath, List<string> excludes)
        {
            var sep = Path.DirectorySeparatorChar;
            if (fullPath.Contains($"{sep}obj{sep}") || fullPath.Contains($"{sep}bin{sep}"))
                return true;
            return excludes.Any(e =>
                fullPath.IndexOf(e, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static void LoadRuleset(string path, HashSet<string> enabled, HashSet<string> disabled)
        {
            var text = File.ReadAllText(path).Trim();
            if (text.StartsWith("{"))
            {
                if (text.Contains("\"enable\""))
                {
                    var start = text.IndexOf('[', text.IndexOf("\"enable\""));
                    var end = text.IndexOf(']', start);
                    if (start >= 0 && end > start)
                        foreach (var r in text.Substring(start + 1, end - start - 1).Split(','))
                            enabled.Add(NormRuleId(r.Trim(' ', '"', '\'')));
                }
                if (text.Contains("\"disable\""))
                {
                    var start = text.IndexOf('[', text.IndexOf("\"disable\""));
                    var end = text.IndexOf(']', start);
                    if (start >= 0 && end > start)
                        foreach (var r in text.Substring(start + 1, end - start - 1).Split(','))
                            disabled.Add(NormRuleId(r.Trim(' ', '"', '\'')));
                }
            }
            else
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    var t = line.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                    if (t.StartsWith("-"))
                        disabled.Add(t.Substring(1).Trim());
                    else
                        enabled.Add(t);
                }
            }
        }

        static async Task<(ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<Diagnostic> ParseErrors)> AnalyzeFilesAsync(
            List<string> files,
            HashSet<string> enabledRules,
            HashSet<string> disabledRules,
            List<string> references)
        {
            var trees = new List<SyntaxTree>();
            var parseErrors = ImmutableArray.CreateBuilder<Diagnostic>();
            // Unity scripts are analyzed in the editor context: define UNITY_EDITOR so that
            // `#if UNITY_EDITOR` blocks are parsed as active code (otherwise Roslyn treats
            // them as skipped trivia and every rule goes blind inside them).
            var parseOptions = new CSharpParseOptions(
                preprocessorSymbols: new[] { "UNITY_EDITOR" });
            foreach (var f in files)
            {
                var text = await File.ReadAllTextAsync(f);
                var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path: f);
                trees.Add(tree);
                // Fail-closed (round 4, 2.1д): a broken parse tree is a blocking
                // result, not a silent pass. Reported as UE000.
                foreach (var pd in tree.GetDiagnostics().Where(d =>
                    d.Severity == DiagnosticSeverity.Error))
                {
                    parseErrors.Add(Diagnostic.Create(
                        ParseErrorRule,
                        Microsoft.CodeAnalysis.Location.Create(tree, pd.Location.SourceSpan),
                        pd.Id, pd.GetMessage()));
                }
            }

            var refs = new List<MetadataReference>
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            };

            try
            {
                var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
                var sysRuntime = Path.Combine(runtimeDir, "System.Runtime.dll");
                if (File.Exists(sysRuntime))
                    refs.Add(MetadataReference.CreateFromFile(sysRuntime));
            }
            catch { }

            foreach (var r in references)
            {
                if (File.Exists(r))
                    refs.Add(MetadataReference.CreateFromFile(Path.GetFullPath(r)));
                else
                    Console.Error.WriteLine($"Warning: reference not found: {r}");
            }

            var compilation = CSharpCompilation.Create(
                "ScriptCheckupAnalysis",
                trees,
                refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
                new UnityRulesAnalyzer(),
                new DataFlowAnalyzer(),
                new ReadbackRulesAnalyzer(),
                new UpdateWasteRulesAnalyzer(),
                new LintRulesAnalyzer(),
                new ReinitRulesAnalyzer(),
                new ErrorRulesAnalyzer());

            var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers,
                new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty));

            var allDiags = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

            // Fail-closed (round 3 review, fixed round 4 item 2.1a): a crashed analyzer
            // surfaces as AD0001 with Warning severity. A gate that treats
            // "warning = pass" would let such a run through silently, so analyzer
            // failures are always blocking and are never filtered out by
            // --disable/--enable. Single pass — no duplicates.
            var filtered = allDiags.Where(d =>
            {
                if (IsAnalyzerFailure(d.Id)) return true;
                if (disabledRules.Contains(d.Id)) return false;
                if (enabledRules.Count > 0 && !enabledRules.Contains(d.Id)) return false;
                return true;
            }).ToImmutableArray();

            return (filtered, parseErrors.ToImmutable());
        }

        static bool IsAnalyzerFailure(string id) =>
            id.StartsWith("AD", StringComparison.Ordinal);

        /// <summary>
        /// Synthetic gate rule: a file that does not parse is a blocking result
        /// (round 4, 2.1д). Not produced by any analyzer — the CLI adds it.
        /// </summary>
        static readonly DiagnosticDescriptor ParseErrorRule = new DiagnosticDescriptor(
            "UE000", "Syntax error", "Syntax error {0}: {1}",
            "ScriptCheckup.Gate", DiagnosticSeverity.Error, isEnabledByDefault: true);

        static void WriteSarif(ImmutableArray<Diagnostic> diagnostics, List<string> files, string outPath)
        {
            var allAnalyzers = new DiagnosticAnalyzer[]
            {
                new UnityRulesAnalyzer(),
                new DataFlowAnalyzer(),
                new ReadbackRulesAnalyzer(),
                new UpdateWasteRulesAnalyzer(),
                new LintRulesAnalyzer(),
                new ReinitRulesAnalyzer(),
                new ErrorRulesAnalyzer(),
            };
            var allRules = allAnalyzers
                .SelectMany(a => a.SupportedDiagnostics)
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .ToList();
            // The synthetic gate rule UE000 is added by the CLI, not by an analyzer.
            allRules.Add(ParseErrorRule);
            // Ruleset version (round 4, 2.2): from the CLI assembly, never hardcoded.
            var asmVersion = typeof(Program).Assembly.GetName().Version;
            var rulesetVersion = asmVersion is null
                ? "0.0.0-unknown"
                : $"{asmVersion.Major}.{asmVersion.Minor}.{asmVersion.Build}";
            var analyzerFailures = diagnostics.Where(d => IsAnalyzerFailure(d.Id)).ToList();
            var root = Directory.GetCurrentDirectory();
            string Rel(string p)
            {
                var rel = Path.GetRelativePath(root, p);
                return rel.Replace(Path.DirectorySeparatorChar, '/');
            }
            var results = diagnostics
                .Select(d =>
                {
                    var span = d.Location.GetLineSpan();
                    var uri = string.IsNullOrEmpty(span.Path) ? "<unknown>" : Rel(span.Path);
                    return new
                    {
                        Diag = d,
                        Uri = uri,
                        Span = span,
                        // Fail-closed (round 4, 2.1б): an analyzer crash is an error in
                        // SARIF even though Roslyn reports it as a warning — the gate
                        // reads SARIF, not the console.
                        Level = (d.Severity == DiagnosticSeverity.Error || IsAnalyzerFailure(d.Id))
                            ? FailureLevel.Error
                            : d.Severity == DiagnosticSeverity.Warning ? FailureLevel.Warning : FailureLevel.Note,
                        Fingerprint = FingerprintOf(d)
                    };
                })
                // Reproducible order (round 4, 2.3): sort by (uri, line, column, rule).
                .OrderBy(x => x.Uri, StringComparer.Ordinal)
                .ThenBy(x => x.Span.StartLinePosition.Line)
                .ThenBy(x => x.Span.StartLinePosition.Character)
                .ThenBy(x => x.Diag.Id, StringComparer.Ordinal)
                .Select(x => new Result
                {
                    RuleId = x.Diag.Id,
                    Level = x.Level,
                    Message = new Message { Text = x.Diag.GetMessage() },
                    PartialFingerprints = new Dictionary<string, string>
                    {
                        ["scriptcheckup/v1"] = x.Fingerprint
                    },
                    Locations = new List<Microsoft.CodeAnalysis.Sarif.Location>
                    {
                        new Microsoft.CodeAnalysis.Sarif.Location
                        {
                            PhysicalLocation = new PhysicalLocation
                            {
                                ArtifactLocation = new ArtifactLocation
                                {
                                    Uri = new Uri(x.Uri, UriKind.Relative),
                                    UriBaseId = "SRCROOT"
                                },
                                Region = new Region
                                {
                                    StartLine = x.Span.StartLinePosition.Line + 1,
                                    StartColumn = x.Span.StartLinePosition.Character + 1,
                                    EndLine = x.Span.EndLinePosition.Line + 1,
                                    EndColumn = x.Span.EndLinePosition.Character + 1
                                }
                            }
                        }
                    }
                }).ToList();
            var log = new SarifLog
            {
                Version = Microsoft.CodeAnalysis.Sarif.SarifVersion.Current,
                SchemaUri = new Uri("https://json.schemastore.org/sarif-2.1.0.json"),
                Runs = new List<Run>
                {
                    new Run
                    {
                        Tool = new Tool
                        {
                            Driver = new ToolComponent
                            {
                                Name = "ScriptCheckup",
                                Version = rulesetVersion,
                                Rules = allRules
                                    .Select(d => new ReportingDescriptor
                                    {
                                        Id = d.Id,
                                        Name = d.Title.ToString(),
                                        ShortDescription = new MultiformatMessageString { Text = d.Title.ToString() },
                                        FullDescription = new MultiformatMessageString { Text = d.Description.ToString() },
                                        DefaultConfiguration = new ReportingConfiguration
                                        {
                                            Level = d.DefaultSeverity switch
                                            {
                                                DiagnosticSeverity.Error => FailureLevel.Error,
                                                DiagnosticSeverity.Warning => FailureLevel.Warning,
                                                _ => FailureLevel.Note
                                            }
                                        }
                                    }).ToList()
                            }
                        },
                        OriginalUriBaseIds = new Dictionary<string, ArtifactLocation>
                        {
                            ["SRCROOT"] = new ArtifactLocation { Uri = new Uri("file:///" + root.Replace(Path.DirectorySeparatorChar, '/').TrimStart('/') + "/") }
                        },
                        Results = results,
                        Artifacts = files.Select(f => new Artifact
                        {
                            Location = new ArtifactLocation
                            {
                                Uri = new Uri(Rel(Path.GetFullPath(f)), UriKind.Relative),
                                UriBaseId = "SRCROOT"
                            }
                        }).ToList(),
                        // Fail-closed (round 4, 2.1б): the run is marked unsuccessful and
                        // carries the analyzer exception text when a crash happened.
                        Invocations = new List<Invocation>
                        {
                            new Invocation
                            {
                                ExecutionSuccessful = analyzerFailures.Count == 0,
                                ToolExecutionNotifications = analyzerFailures.Select(d =>
                                    new Notification
                                    {
                                        Level = FailureLevel.Error,
                                        Message = new Message { Text = d.GetMessage() },
                                        AssociatedRule = new ReportingDescriptorReference { Id = d.Id }
                                    }).ToList()
                            }
                        }
                    }
                }
            };

            File.WriteAllText(outPath, Newtonsoft.Json.JsonConvert.SerializeObject(log, Newtonsoft.Json.Formatting.Indented));
        }

        /// <summary>
        /// Stable fingerprint for waivers (round 4, 2.3): hash of rule id +
        /// normalized source line + enclosing symbol name. Survives line shifts.
        /// </summary>
        static string FingerprintOf(Diagnostic d)
        {
            string lineText = "";
            string symbolName = "";
            try
            {
                var tree = d.Location.SourceTree;
                if (tree is not null)
                {
                    var span = d.Location.GetLineSpan();
                    var text = tree.GetText();
                    var line = text.Lines[span.StartLinePosition.Line];
                    lineText = System.Text.RegularExpressions.Regex.Replace(
                        line.ToString().Trim(), @"\s+", " ");
                    var node = tree.GetRoot().FindNode(d.Location.SourceSpan);
                    symbolName = node.AncestorsAndSelf()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault()?.Identifier.ValueText
                        ?? node.AncestorsAndSelf()
                            .OfType<TypeDeclarationSyntax>()
                            .FirstOrDefault()?.Identifier.ValueText
                        ?? "";
                }
            }
            catch { }
            var raw = d.Id + "|" + lineText + "|" + symbolName;
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()[..16];
        }
    }
}
