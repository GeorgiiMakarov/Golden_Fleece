// Program.cs — LayerSwitcher emulator: JSON gesture stream -> LayerSwitcher.
//
//   dotnet run -- data/sample_manifest_demo.json data/sample_gesture_stream.json
//
// Replays synthetic 26-joint frames through GestureMath, drives the headless
// LayerSwitcherModel, and prints a PASS/FAIL report:
//   * per labeled segment: exactly one expected detection inside the window
//   * false positives outside labeled windows: must be zero
//   * max per-frame classify time and max switch dispatch vs the 100 ms budget
//
// Exit code 0 = all PASS, 1 = any FAIL.
using System.Diagnostics;
using System.Text.Json;
using XrLayerSwitcherEmulator;

return Emulator.Run(args);

sealed class Segment
{
    public string Label = "";
    public GestureType Expected;
    public long StartMs;
    public long EndMs;
}

static class Emulator
{
    const long WindowGraceMs = 300;

    public static int Run(string[] args)
    {
        string manifestPath = args.Length > 0 ? args[0] : "data/sample_manifest_demo.json";
        string streamPath = args.Length > 1 ? args[1] : "data/sample_gesture_stream.json";

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var manifest = JsonSerializer.Deserialize<ManifestFile>(
            File.ReadAllText(manifestPath), jsonOptions)
            ?? throw new InvalidDataException("manifest is empty");
        if (manifest.GestureDictionaryVersion != "v1")
        {
            Console.WriteLine($"FAIL: gestureDictionaryVersion '{manifest.GestureDictionaryVersion}' " +
                              "does not match runtime v1 — refusing to run.");
            return 1;
        }

        var stream = JsonSerializer.Deserialize<GestureStream>(
            File.ReadAllText(streamPath), jsonOptions)
            ?? throw new InvalidDataException("stream is empty");

        var swipe = new SwipeDetector();
        var hold = new PalmHoldDetector();
        var switcher = new LayerSwitcherModel(manifest.Layers.Count);
        var detections = new List<(long tMs, GestureType g)>();
        double maxClassifyMs = 0;
        var sw = new Stopwatch();

        int spawned = 0;
        foreach (var f in stream.Frames.OrderBy(f => f.TMs))
        {
            float t = f.TMs / 1000f;

            // Video-clock spawns (Phase 2 behavior; Phase 1: manual/test).
            while (spawned < manifest.Layers.Count &&
                   manifest.Layers[spawned].SpawnAtMs <= f.TMs)
            {
                switcher.ShowLayer(spawned);
                Console.WriteLine($"[{f.TMs,6} ms] clock -> spawn " +
                                  $"{manifest.Layers[spawned].LayerId} " +
                                  $"(visible layer {switcher.VisibleLayer})");
                spawned++;
            }

            var j = f.Joints[0];
            var palm = new Vec3(j[0], j[1], j[2]);

            sw.Restart();
            var g1 = swipe.Update(palm, t, f.Tracked);
            var g2 = hold.Update(palm, t, f.Tracked);
            sw.Stop();
            double classifyMs = sw.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            if (classifyMs > maxClassifyMs) maxClassifyMs = classifyMs;

            foreach (var g in new[] { g1, g2 })
            {
                if (g is not { } gesture || gesture == GestureType.None) continue;
                var (visible, switchMs) = switcher.OnGesture(gesture);
                detections.Add((f.TMs, gesture));
                Console.WriteLine($"[{f.TMs,6} ms] {gesture} -> visible layer " +
                                  $"{visible} (dispatch {switchMs:F3} ms)");
            }
        }

        return Report(stream, detections, maxClassifyMs, switcher.MaxSwitchMs);
    }

    static GestureType ParseGesture(string s) =>
        Enum.TryParse<GestureType>(s, out var g) ? g : GestureType.None;

    static int Report(GestureStream stream,
                      List<(long tMs, GestureType g)> detections,
                      double maxClassifyMs, double maxSwitchMs)
    {
        // Labeled segments: contiguous runs with expectedGesture != None.
        var segments = new List<Segment>();
        Segment? cur = null;
        foreach (var f in stream.Frames)
        {
            var exp = ParseGesture(f.ExpectedGesture);
            if (exp != GestureType.None && (cur == null || cur.Label != f.Label))
            {
                if (cur != null) segments.Add(cur);
                cur = new Segment
                {
                    Label = f.Label, Expected = exp,
                    StartMs = f.TMs, EndMs = f.TMs,
                };
            }
            else if (exp != GestureType.None && cur != null)
            {
                cur.EndMs = f.TMs;
            }
            else if (cur != null)
            {
                segments.Add(cur);
                cur = null;
            }
        }
        if (cur != null) segments.Add(cur);

        bool InWindow((long tMs, GestureType g) d, Segment s) =>
            d.tMs >= s.StartMs - 50 && d.tMs <= s.EndMs + WindowGraceMs;

        bool ok = true;
        Console.WriteLine();
        Console.WriteLine($"frames: {stream.Frames.Count}, detections: " +
                          string.Join(", ", detections.Select(d => $"{d.g}@{d.tMs}ms")));
        foreach (var s in segments)
        {
            int hits = detections.Count(d => d.g == s.Expected && InWindow(d, s));
            bool pass = hits == 1;
            ok &= pass;
            Console.WriteLine($"[{(pass ? "PASS" : "FAIL")}] {s.Label}: " +
                              $"expected 1x {s.Expected}, got {hits}");
        }

        int falsePositives = detections.Count(d => !segments.Any(s => InWindow(d, s)));
        bool fpPass = falsePositives == 0;
        ok &= fpPass;
        Console.WriteLine($"[{(fpPass ? "PASS" : "FAIL")}] false positives: {falsePositives}");

        bool budgetPass = maxSwitchMs < LayerSwitcherModel.SwitchBudgetMs;
        ok &= budgetPass;
        Console.WriteLine($"max classify/frame: {maxClassifyMs:F3} ms, " +
                          $"max switch dispatch: {maxSwitchMs:F3} ms " +
                          $"(budget {LayerSwitcherModel.SwitchBudgetMs} ms) -> " +
                          $"{(budgetPass ? "PASS" : "FAIL")}");
        Console.WriteLine(ok ? "RESULT: ALL PASS" : "RESULT: FAILURES PRESENT");
        return ok ? 0 : 1;
    }
}
