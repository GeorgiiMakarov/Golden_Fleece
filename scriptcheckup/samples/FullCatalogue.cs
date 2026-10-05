// Samples for the second-wave rules (v2.3): RB004/RB006-RB011, UW002-UW006,
// UW008-UW011, UL001-UL007, UL009, UL011, RX002-RX006, RX008, UE003.
// Expected diagnostics are marked with // !<RULE>.

using UnityEngine;
using UnityEngine.SceneManagement;
using System.Threading;

// UL009: class not inheriting MonoBehaviour
public class PlainConfig // !UL009
{
    public void Do() { }
}

public class Wasteful : MonoBehaviour
{
    public int health; // !UL001
    public static Wasteful Instance;
    public static event System.Action OnWaveCleared; // !RX003 (no unsub anywhere)
    public static int spawnedTotal;

    void Awake()
    {
        Instance = this; // !RX002 (never nulled)
        OnWaveCleared += ShowVictory; // !UW007 (no unsub in teardown)
        DontDestroyOnLoad(gameObject); // !RX005 (no duplicate guard)
    }

    void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded; // !RX008 (no unsub)
    }

    void Update()
    {
        var e = FindObjectOfType<Enemy>(); // !UW001 !DF001
        var g = GameObject.Find("Chest"); // !UW002 !DF001
        Camera.main.transform.position = transform.position; // !UW003
        transform.Translate(Vector3.up * 5f); // !UW005 (no deltaTime)
        transform.Translate(Vector3.up * 5f * Time.deltaTime); // clean: deltaTime present
        Debug.Log("hp: " + health); // !UL002 !UL007
        var xs = new System.Collections.Generic.List<int>(); // !UL006
        var v = new Vector3(1, 2, 3); // clean: value type, no heap allocation
        GetComponent<Rigidbody>(); // !UW011
        var rb = GetComponent<Rigidbody>(); // !UW011
        rb.AddForce(Vector3.up); // !UW004
        if (gameObject.tag == "Player") { } // !UW006
        Resources.Load("Chest"); // !UW009 !DF001
        var tex = new Texture2D(64, 64); // !DF001
        tex.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); // !RB001
        tex.Apply(); // !RB007
        var buf = new ComputeBuffer(8, 4); // !DF001
        buf.GetData(new int[8]); // !RB006
        ScreenCapture.CaptureScreenshot("s.png"); // !RB008
        StartCoroutine("Fade"); // !UL003
        Invoke("Respawn", 1f); // !UL004
        SendMessage("Ping"); // !UL005
    }

    void FixedUpdate()
    {
        GetComponent<Rigidbody>().AddForce(Vector3.up); // !UW011 only (physics OK in FixedUpdate)
    }

    void Start()
    {
        var g = GameObject.Find("Spawn"); // !UL011 (outside Update) !DF001
    }

    async void LoadPanel() // !UW008
    {
    }

    void Freeze() { Thread.Sleep(50); } // !UE003

    void ShowVictory() { }
    void OnSceneLoaded(Scene s, LoadSceneMode m) { }

    void OnDestroy()
    {
        AsyncGPUReadback.WaitAllRequests(); // !RB004(info: teardown)
        spawnedTotal++; // !RX004
    }

    Camera cam;
    void RenderToTarget()
    {
        cam.targetTexture = new RenderTexture(256, 256, 0); // !RB010(info: not hot)
        var raw = new Texture2D(8, 8).GetRawTextureData<byte>(); // !RB011 !DF001
        var px = new Texture2D(8, 8).GetPixels(); // !RB009(info: not hot) !DF001
    }
}

#if UNITY_EDITOR
public class EditorNote : MonoBehaviour
{
    void OnGUI() { UnityEditor.EditorUtility.SetDirty(this); } // !RX006
}
#endif

// Negative: guarded singleton — clean (no RX002/RX005)
public class GuardedSingleton : MonoBehaviour
{
    public static GuardedSingleton Instance;
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject); // clean: duplicate guard present
    }
    void OnDestroy() { Instance = null; } // clean: nulled
}
