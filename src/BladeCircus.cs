using System.Collections.Generic;
using UnityEngine;

// BLADE CIRCUS — a one-tap 3D carnival knife-throwing game (Knife Hit core, big-top theme).
// A painted target WHEEL spins in the spotlight. The ONLY control is THROW (tap / click / Space / Up):
// a knife flies up from below and embeds in the wheel at the exact angle the rim is passing the bottom.
// Stuck knives ride the wheel as it spins — land your next blade where one already sticks and the blade
// clangs off: GAME OVER. Land them all to clear the wheel and move on to a faster, fuller, trickier one.
// Pinned apples on the rim can be SPLIT for coin bonuses + combo. Boss wheels (steel) every 5 stages
// spin erratically and demand a full quiver. Tense within ten seconds, juicy on every thunk.
//
// Built entirely in code (CreatePrimitive + procedural placement) so it renders reliably in WebGL with
// engine-code stripping disabled. NO Rigidbody/colliders: the wheel is pure Transform rotation and every
// hit is an angular test in the wheel's own rotating frame. Coexists with the permanent Juice & AutoShot.
public class BladeCircus : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__BladeCircus");
        go.AddComponent<BladeCircus>();
        DontDestroyOnLoad(go);
    }

    // ---------------------------------------------------------------- tuning
    const float R = 1.9f;                                  // wheel radius
    static readonly Vector3 WHEEL_POS = new Vector3(0f, 1.65f, 0f);
    const float KNIFE_Z = -0.95f;                          // knife plane (in front of painted face)
    const float FRONT_Z = -0.74f;                          // face base plane
    const float TIP_R = R - 0.75f;                         // blade-tip radius from centre (1.15)
    const float BLADE_OUT = R + 0.18f;                     // where the blade exits the rim (2.08)
    const float TIP_TOP = TIP_R - 0.15f;                   // dist from pivot to the very tip (~1.0)
    const float FLY_SPEED = 19f;                           // knife flight speed (units/s)
    const float SPAWN_Y = -0.55f;                          // ready-knife pivot rest Y (tip ~ -1.55)
    const float STICK_Y = (WHEEL_POS_Y - R) + TIP_TOP;     // pivot Y at which the tip meets the bottom rim
    const float WHEEL_POS_Y = 1.65f;
    const float COLLIDE_DEG = 12.5f;                       // min gap between blades; closer = clang
    const float APPLE_DEG = 9.0f;                          // split window
    const int KNIFE_POOL = 16;
    const int APPLE_POOL = 4;
    const int MAX_TOTAL = 10;                              // max blades on a wheel (keeps it solvable)

    // ---------------------------------------------------------------- scene refs
    Transform camT; Camera camComp;
    Transform wheelT;                 // spins about Z; stuck knives + apples are children
    Transform flyKnifeT;              // the single in-flight / ready knife (world space)
    Transform readyHintT;             // glow marking the throw lane
    TextMesh hudStage, hudScore, hudBest, hudKnives, comboText, bannerText, dbg;

    Material steelMat, handleMat, handleMat2, faceMat, faceMat2, rimMat, boltMat,
             hubA, hubB, hubC, appleMat, leafMat, stemMat, spotMat, dividerMat;

    // ---------------------------------------------------------------- run state
    enum State { Playing, Clear, Dead }
    State state = State.Playing;
    enum Throw { Ready, Flying }
    Throw thr = Throw.Ready;

    int stage = 1;
    int knivesToLand, knivesLanded;
    int score, best, coins, combo, bestCombo;
    bool attract = true;              // auto-demo until first real input
    bool showDbg, isBoss;

    // wheel spin model: angVel(t) = dir*(base + amp*sin(t*freq))
    float logAngle, spinBase, spinAmp, spinFreq, spinDir, spinT;

    float flyY;                       // pivot Y of the flying/ready knife
    float clearTimer, deathTimer, comboFlash;
    float deathVy, deathSpin;

    readonly List<float> occupied = new List<float>();    // log-local angles currently occupied (deg)
    readonly List<Transform> stuckPool = new List<Transform>();
    int stuckUsed;

    class Apple { public Transform t; public float a; public bool alive; }
    readonly List<Apple> apples = new List<Apple>();

    // HUD layout (aspect-adaptive)
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;

    // ===================================================================== boot
    void Start()
    {
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("bladecircus_best", 0);
        bestCombo = PlayerPrefs.GetInt("bladecircus_bestcombo", 0);

        BuildMaterials();
        BuildEnvironment();
        BuildCamera();
        BuildWheel();
        BuildKnifePools();
        BuildHud();

        stage = 1; score = 0; coins = 0; combo = 0;
        NewStage(true);
    }

    // ===================================================================== materials
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.3f, bool emissive = false, float alpha = 1f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        c.a = alpha;
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor")) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", c * 0.8f); }
        if (alpha < 1f) SetTransparent(m, c);
        return m;
    }

    static void SetTransparent(Material m, Color c)
    {
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.DisableKeyword("_ALPHATEST_ON");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
    }

    void BuildMaterials()
    {
        steelMat   = Mat(new Color(0.86f, 0.89f, 0.96f), 0.25f, 0.62f);  // low metallic: no reflection probe, so keep it diffuse-bright
        handleMat  = Mat(new Color(0.80f, 0.22f, 0.22f), 0.1f, 0.45f);
        handleMat2 = Mat(new Color(0.18f, 0.32f, 0.58f), 0.1f, 0.45f);
        faceMat    = Mat(new Color(0.95f, 0.90f, 0.78f), 0.05f, 0.25f);   // cream
        faceMat2   = Mat(new Color(0.82f, 0.24f, 0.28f), 0.05f, 0.25f);   // carnival red
        rimMat     = Mat(new Color(0.46f, 0.30f, 0.18f), 0.15f, 0.4f);    // wood rim
        boltMat    = Mat(new Color(0.96f, 0.82f, 0.35f), 0.85f, 0.85f, true);
        hubA       = Mat(new Color(0.86f, 0.24f, 0.28f), 0.1f, 0.4f);
        hubB       = Mat(new Color(0.97f, 0.94f, 0.86f), 0.05f, 0.3f);
        hubC       = Mat(new Color(0.96f, 0.82f, 0.35f), 0.6f, 0.7f, true);   // bright gold centre (no dark cavity)
        appleMat   = Mat(new Color(0.90f, 0.18f, 0.20f), 0.1f, 0.7f);
        leafMat    = Mat(new Color(0.30f, 0.72f, 0.32f), 0.1f, 0.5f);
        stemMat    = Mat(new Color(0.35f, 0.24f, 0.14f), 0.1f, 0.3f);
        spotMat    = Mat(new Color(1f, 0.96f, 0.8f, 0.10f), 0f, 0.2f, true, 0.10f);
        dividerMat = Mat(new Color(0.30f, 0.22f, 0.16f), 0.1f, 0.3f);
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Material shared)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared;
        return g;
    }

    // ===================================================================== environment
    void BuildEnvironment()
    {
        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(1f, 0.96f, 0.88f);
        sun.intensity = 1.0f;
        sun.transform.rotation = Quaternion.Euler(38f, -14f, 0f);
        sun.shadows = LightShadows.None;        // knives sticking out cast ugly blobs on the face — keep it clean

        var spot = new GameObject("Spot").AddComponent<Light>();
        spot.type = LightType.Spot;
        spot.color = new Color(1f, 0.93f, 0.74f);
        spot.intensity = 2.6f;
        spot.range = 28f; spot.spotAngle = 62f;
        spot.transform.position = new Vector3(0.3f, 7.5f, -7.0f);
        spot.transform.rotation = Quaternion.Euler(44f, -2f, 0f);

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.30f, 0.28f, 0.44f);
        RenderSettings.ambientEquatorColor = new Color(0.22f, 0.20f, 0.30f);
        RenderSettings.ambientGroundColor = new Color(0.10f, 0.09f, 0.14f);

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.10f, 0.08f, 0.16f);
        RenderSettings.fogStartDistance = 16f;
        RenderSettings.fogEndDistance = 52f;

        var back = Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(70f, 46f, 0.6f),
            Mat(new Color(0.13f, 0.10f, 0.21f), 0.0f, 0.1f));
        back.transform.position = new Vector3(0, 5f, 8f);

        // big-top floor stripes far below
        for (int i = -8; i <= 8; i++)
            Prim(PrimitiveType.Cube, null, Vector3.zero, new Vector3(1.3f, 0.3f, 30f),
                (i % 2 == 0) ? Mat(new Color(0.70f, 0.20f, 0.22f), 0.05f, 0.2f) : Mat(new Color(0.92f, 0.88f, 0.78f), 0.05f, 0.2f))
                .transform.SetPositionAndRotation(new Vector3(i * 1.3f, -4.6f, 4f), Quaternion.identity);

        // soft glow halo behind the wheel
        var halo = Prim(PrimitiveType.Quad, null, Vector3.zero, new Vector3(14f, 14f, 1f), spotMat);
        halo.transform.position = new Vector3(WHEEL_POS.x, WHEEL_POS.y, 4.4f);
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.07f, 0.05f, 0.12f);
        camComp.fieldOfView = 47f;
        camComp.farClipPlane = 120f;
        cgo.AddComponent<AudioListener>();
        camT = cgo.transform;
        camT.rotation = Quaternion.Euler(1.2f, 0f, 0f);   // near head-on; knives give the depth
        UpdateCameraRig();
    }

    // Pull the camera back just enough that the whole wheel (and most of the radiating handles) fits the
    // width on any aspect — so a tall phone doesn't shove knife handles off the sides — but stay close on
    // wide screens for a punchy framing.
    void UpdateCameraRig()
    {
        if (camComp == null || camT == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        float halfVtan = Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        const float TARGET_HALF_W = 2.75f;                // world half-width to keep on screen
        float dist = TARGET_HALF_W / Mathf.Max(0.05f, halfVtan * aspect);
        dist = Mathf.Clamp(dist, 9.4f, 14.5f);
        camT.position = new Vector3(0f, WHEEL_POS.y - 0.10f, -dist);
    }

    // ===================================================================== wheel
    void BuildWheel()
    {
        wheelT = new GameObject("Wheel").transform;
        wheelT.position = WHEEL_POS;

        // wood rim/body
        var body = Prim(PrimitiveType.Cylinder, wheelT, Vector3.zero, new Vector3(R * 2f, 0.55f, R * 2f), rimMat);
        body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // concentric target rings (a dartboard face) — each a flat disk slightly in front of the last,
        // so the centre is always a solid bright bullseye (no dark cavity).
        AddDisk(R * 0.97f, FRONT_Z - 0.00f, faceMat);   // cream field
        AddDisk(R * 0.80f, FRONT_Z - 0.03f, faceMat2);  // red ring
        AddDisk(R * 0.62f, FRONT_Z - 0.06f, faceMat);   // cream ring
        AddDisk(R * 0.44f, FRONT_Z - 0.09f, faceMat2);  // red ring
        AddDisk(R * 0.26f, FRONT_Z - 0.12f, hubB);      // cream
        AddDisk(R * 0.12f, FRONT_Z - 0.15f, hubC);      // gold bullseye

        // 8 dark sector dividers radiating from the rings
        for (int i = 0; i < 8; i++)
        {
            var bar = Prim(PrimitiveType.Cube, wheelT, new Vector3(0, 0, FRONT_Z - 0.16f),
                new Vector3(0.06f, R * 1.7f, 0.04f), dividerMat);
            bar.transform.localRotation = Quaternion.Euler(0, 0, i * 22.5f);
        }

        // brass bolts around the rim
        for (int i = 0; i < 12; i++)
        {
            float a = (i * 30f + 15f) * Mathf.Deg2Rad;
            Prim(PrimitiveType.Sphere, wheelT, new Vector3(Mathf.Cos(a) * R * 0.90f, Mathf.Sin(a) * R * 0.90f, FRONT_Z - 0.18f),
                Vector3.one * 0.15f, boltMat);
        }
    }

    void AddDisk(float rad, float z, Material m)
    {
        var d = Prim(PrimitiveType.Cylinder, wheelT, new Vector3(0, 0, z), new Vector3(rad * 2f, 0.04f, rad * 2f), m);
        d.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
    }

    // ===================================================================== knives
    // A knife is built along the local -Y axis (the "outward" radial when stuck at the bottom, rot 0).
    // The tip sits near the rim (radius TIP_R), the blade crosses the rim, the handle hangs outside.
    Transform BuildKnife(Transform parent, bool flying)
    {
        var root = new GameObject(flying ? "FlyKnife" : "Knife").transform;
        if (parent != null) root.SetParent(parent, false);

        float bMid = (TIP_R + BLADE_OUT) * 0.5f, bLen = BLADE_OUT - TIP_R;
        Prim(PrimitiveType.Cube, root, new Vector3(0, -bMid, 0), new Vector3(0.16f, bLen, 0.09f), steelMat);        // blade
        Prim(PrimitiveType.Cube, root, new Vector3(0, -(TIP_R - 0.10f), 0), new Vector3(0.05f, 0.22f, 0.05f), steelMat); // sharpened tip
        Prim(PrimitiveType.Cube, root, new Vector3(0, -(BLADE_OUT + 0.06f), 0), new Vector3(0.34f, 0.10f, 0.16f), boltMat); // guard
        float hOut = BLADE_OUT + 0.62f;
        Prim(PrimitiveType.Cube, root, new Vector3(0, -hOut, 0), new Vector3(0.20f, 0.95f, 0.20f), flying ? handleMat : (Random.value < 0.5f ? handleMat : handleMat2)); // handle
        Prim(PrimitiveType.Sphere, root, new Vector3(0, -(hOut + 0.52f), 0), Vector3.one * 0.22f, boltMat);        // pommel
        return root;
    }

    void BuildKnifePools()
    {
        for (int i = 0; i < KNIFE_POOL; i++)
        {
            var k = BuildKnife(wheelT, false);
            k.gameObject.SetActive(false);
            stuckPool.Add(k);
        }
        flyKnifeT = BuildKnife(null, true);

        readyHintT = Prim(PrimitiveType.Quad, null, Vector3.zero, new Vector3(0.7f, 2.2f, 1f),
            Mat(new Color(1f, 0.9f, 0.5f, 0.16f), 0f, 0.2f, true, 0.16f)).transform;

        for (int i = 0; i < APPLE_POOL; i++)
        {
            var rootGo = new GameObject("Apple");
            var root = rootGo.transform;
            root.SetParent(wheelT, false);
            var body = Prim(PrimitiveType.Sphere, root, Vector3.zero, new Vector3(0.44f, 0.42f, 0.44f), appleMat);
            Prim(PrimitiveType.Cube, root, new Vector3(0, 0.27f, 0), new Vector3(0.05f, 0.18f, 0.05f), stemMat);
            Prim(PrimitiveType.Quad, root, new Vector3(0.16f, 0.32f, -0.02f), new Vector3(0.3f, 0.18f, 1f), leafMat)
                .transform.localRotation = Quaternion.Euler(0, 0, 35f);
            root.gameObject.SetActive(false);
            apples.Add(new Apple { t = root, alive = false });
        }
    }

    // ===================================================================== stage lifecycle
    void NewStage(bool first)
    {
        state = State.Playing;
        thr = Throw.Ready;
        clearTimer = 0f; deathTimer = 0f;
        hudStage.gameObject.SetActive(true);
        hudScore.gameObject.SetActive(true);
        hudKnives.gameObject.SetActive(true);
        knivesLanded = 0;
        occupied.Clear();
        stuckUsed = 0;
        foreach (var k in stuckPool) k.gameObject.SetActive(false);
        foreach (var a in apples) { a.alive = false; a.t.gameObject.SetActive(false); }

        isBoss = (stage % 5 == 0);
        knivesToLand = isBoss ? Mathf.Min(7 + stage / 5, 10) : Mathf.Min(3 + stage, 8);

        int preMax = isBoss ? 5 : Mathf.Min(stage - 1, 4);
        int pre = Mathf.Clamp(preMax, 0, Mathf.Max(0, MAX_TOTAL - knivesToLand));

        float t = Mathf.Clamp01((stage - 1) / 12f);
        spinBase = Mathf.Lerp(56f, 158f, t) + (isBoss ? 34f : 0f);
        spinAmp  = (stage >= 4 || isBoss) ? Mathf.Lerp(18f, 88f, t) + (isBoss ? 36f : 0f) : 0f;
        spinFreq = Random.Range(0.7f, 1.5f) + (isBoss ? 0.5f : 0f);
        spinDir  = (stage >= 3 && Random.value < 0.5f) ? -1f : 1f;
        spinT = 0f; logAngle = 0f;
        wheelT.localRotation = Quaternion.identity;

        PlacePreStuck(pre);

        int nApples = first ? 1 : Random.Range(stage >= 2 ? 1 : 0, 3);
        PlaceApples(Mathf.Min(nApples, APPLE_POOL));

        thr = Throw.Ready;
        flyY = SPAWN_Y;
        flyKnifeT.gameObject.SetActive(true);
        flyKnifeT.rotation = Quaternion.identity;
        PlaceFlyKnife();

        RefreshHud();
        Banner(isBoss ? "BOSS WHEEL " + stage : "STAGE " + stage,
               isBoss ? new Color(0.8f, 0.85f, 1f) : new Color(1f, 0.92f, 0.6f), 1.1f);
    }

    void PlacePreStuck(int n)
    {
        int placed = 0, guard = 0;
        while (placed < n && guard++ < 500)
        {
            float a = Random.Range(0f, 360f);
            if (!Free(a, COLLIDE_DEG * 1.7f)) continue;
            AddStuckVisual(a);
            occupied.Add(a);
            placed++;
        }
    }

    void PlaceApples(int n)
    {
        int placed = 0, guard = 0;
        while (placed < n && guard++ < 500)
        {
            var ap = apples[placed];
            float a = Random.Range(0f, 360f);
            if (!Free(a, COLLIDE_DEG * 2.2f)) continue;
            bool clash = false;
            for (int i = 0; i < placed; i++) if (AngDiff(a, apples[i].a) < 28f) { clash = true; break; }
            if (clash) continue;
            ap.a = a; ap.alive = true;
            ap.t.gameObject.SetActive(true);
            ap.t.localPosition = LocalDir(a) * (R * 0.82f) + new Vector3(0, 0, KNIFE_Z - 0.05f);
            ap.t.localRotation = Quaternion.identity;
            placed++;
        }
    }

    static Vector3 LocalDir(float deg)
    {
        float r = deg * Mathf.Deg2Rad;
        return new Vector3(Mathf.Cos(r), Mathf.Sin(r), 0f);
    }

    bool Free(float a, float gap)
    {
        for (int i = 0; i < occupied.Count; i++) if (AngDiff(a, occupied[i]) < gap) return false;
        return true;
    }

    void AddStuckVisual(float localAngle)
    {
        if (stuckUsed >= stuckPool.Count) return;
        var k = stuckPool[stuckUsed++];
        k.gameObject.SetActive(true);
        // knife built along -Y (outward = down). Rotate so outward points to localAngle.
        k.localRotation = Quaternion.Euler(0, 0, localAngle + 90f);
        k.localPosition = new Vector3(0, 0, KNIFE_Z);
    }

    // ===================================================================== input
    bool ThrowPressed()
    {
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) return true;
        if (Input.GetMouseButtonDown(0)) return true;
        for (int i = 0; i < Input.touchCount; i++)
            if (Input.GetTouch(i).phase == TouchPhase.Began) return true;
        return false;
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        bool pressed = ThrowPressed();
        if (pressed && attract && state == State.Playing) { attract = false; pressed = false; } // first tap just wakes it

        spinT += dt;
        float angVel = spinDir * (spinBase + spinAmp * Mathf.Sin(spinT * spinFreq));
        logAngle += angVel * dt;
        wheelT.localRotation = Quaternion.Euler(0, 0, logAngle);

        switch (state)
        {
            case State.Playing: TickPlaying(dt, pressed); break;
            case State.Clear:   TickClear(dt); break;
            case State.Dead:    TickDead(dt, pressed); break;
        }

        if (comboFlash > 0f) comboFlash -= dt * 2.2f;
        TickBanner(dt);
        UpdateCameraRig();
        AdjustHud();
        if (showDbg) UpdateDbg(angVel);
    }

    void TickPlaying(float dt, bool pressed)
    {
        bool fire = pressed;
        if (attract && thr == Throw.Ready) fire = AutoShouldThrow();

        if (thr == Throw.Ready)
        {
            flyY = SPAWN_Y + Mathf.Sin(Time.time * 4f) * 0.05f;
            PlaceFlyKnife();
            readyHintT.gameObject.SetActive(true);
            readyHintT.position = new Vector3(0f, WHEEL_POS.y - R - 1.0f, KNIFE_Z + 0.1f);
            float pulse = 1f + Mathf.Sin(Time.time * 6f) * 0.12f;
            readyHintT.localScale = new Vector3(0.7f, 2.2f * pulse, 1f);
            if (fire) { thr = Throw.Flying; Juice.Blip(520f, 0.05f, 0.28f); }
        }
        else // Flying
        {
            readyHintT.gameObject.SetActive(false);
            flyY += FLY_SPEED * dt;
            PlaceFlyKnife();
            if (flyY >= STICK_Y) Stick();
        }
    }

    void PlaceFlyKnife()
    {
        flyKnifeT.position = new Vector3(0f, flyY, KNIFE_Z);
        flyKnifeT.rotation = Quaternion.identity;
    }

    // log-local angle where the bottom contact lands (world contact dir = -90deg)
    float ContactLocalAngle() { return Norm(-90f - logAngle); }

    void Stick()
    {
        float a = ContactLocalAngle();

        for (int i = 0; i < occupied.Count; i++)
            if (AngDiff(a, occupied[i]) < COLLIDE_DEG) { GameOver(); return; }

        AddStuckVisual(a);
        occupied.Add(a);
        knivesLanded++;
        score += 10;

        bool split = false;
        for (int i = 0; i < apples.Count; i++)
        {
            var ap = apples[i];
            if (!ap.alive) continue;
            if (AngDiff(a, ap.a) < APPLE_DEG)
            {
                ap.alive = false;
                ap.t.gameObject.SetActive(false);
                combo++;
                if (combo > bestCombo) { bestCombo = combo; PlayerPrefs.SetInt("bladecircus_bestcombo", bestCombo); }
                int gain = 25 * combo;
                score += gain; coins++;
                Vector3 wp = wheelT.TransformPoint(LocalDir(ap.a) * (R * 0.82f) + new Vector3(0, 0, KNIFE_Z - 0.05f));
                Juice.Score(wp);
                Juice.Pop(wp, new Color(0.95f, 0.25f, 0.25f), 12);
                Juice.Blip(720f + Mathf.Min(combo, 10) * 40f, 0.07f, 0.4f);
                comboText.text = "SPLIT! x" + combo + "   +" + gain;
                comboFlash = 1f;
                split = true;
            }
        }
        if (!split) { combo = 0; comboText.text = ""; }

        Vector3 hit = new Vector3(0f, WHEEL_POS.y - R, KNIFE_Z);
        Juice.Pop(hit, new Color(0.85f, 0.7f, 0.4f), 6);
        Juice.Blip(190f, 0.09f, 0.45f);
        Juice.Shake(0.10f);

        if (score > best) { best = score; PlayerPrefs.SetInt("bladecircus_best", best); }
        RefreshHud();

        if (knivesLanded >= knivesToLand) { StageClear(); return; }

        thr = Throw.Ready;
        flyY = SPAWN_Y;
        PlaceFlyKnife();
    }

    void StageClear()
    {
        state = State.Clear;
        clearTimer = 0f;
        flyKnifeT.gameObject.SetActive(false);
        readyHintT.gameObject.SetActive(false);
        int bonus = 50 + stage * 10;
        score += bonus;
        if (score > best) { best = score; PlayerPrefs.SetInt("bladecircus_best", best); PlayerPrefs.Save(); }
        Juice.Score(WHEEL_POS);
        Juice.Blip(660f, 0.1f, 0.4f); Juice.Blip(880f, 0.1f, 0.4f); Juice.Blip(1180f, 0.12f, 0.4f);
        Banner("WHEEL CLEAR!   +" + bonus, new Color(0.6f, 1f, 0.7f), 1.3f);
        RefreshHud();
    }

    void TickClear(float dt)
    {
        clearTimer += dt;
        if (clearTimer >= 1.2f) { stage++; NewStage(false); }
    }

    void GameOver()
    {
        if (state == State.Dead) return;
        state = State.Dead;
        deathTimer = 0f;
        deathVy = 5.5f;
        deathSpin = (Random.value < 0.5f) ? 540f : -540f;
        combo = 0;
        Juice.Lose();
        Vector3 hit = new Vector3(0f, WHEEL_POS.y - R, KNIFE_Z);
        Juice.Pop(hit, new Color(0.9f, 0.9f, 1f), 14);
        Juice.Blip(120f, 0.18f, 0.5f);
        if (score > best) best = score;
        PlayerPrefs.SetInt("bladecircus_best", Mathf.Max(best, PlayerPrefs.GetInt("bladecircus_best", 0)));
        PlayerPrefs.Save();
        Banner("CLANG!   GAME OVER\nStage " + stage + "    Score " + score + "\nTAP / R to retry", Color.white, 999f);
        comboText.text = "";
        // clean game-over screen: hide the corner stats behind the banner
        hudStage.gameObject.SetActive(false);
        hudScore.gameObject.SetActive(false);
        hudKnives.gameObject.SetActive(false);
        RefreshHud();
    }

    void TickDead(float dt, bool pressed)
    {
        deathTimer += dt;
        deathVy -= 24f * dt;
        flyY += deathVy * dt;
        flyKnifeT.position = new Vector3(flyKnifeT.position.x - 0.9f * dt, flyY, KNIFE_Z);
        flyKnifeT.Rotate(0, 0, deathSpin * dt, Space.Self);

        if (deathTimer > 0.4f && (Input.GetKeyDown(KeyCode.R) || pressed))
        {
            stage = 1; score = 0; coins = 0; combo = 0;
            flyKnifeT.rotation = Quaternion.identity;
            NewStage(true);
            attract = false;   // player retried — hand them control immediately, no demo
        }
    }

    // ===================================================================== auto-demo brain
    float autoCooldown;
    bool AutoShouldThrow()
    {
        autoCooldown -= Time.deltaTime;
        if (autoCooldown > 0f) return false;
        // predict the local angle the blade will actually stick at (account for flight-time drift)
        float flight = (STICK_Y - SPAWN_Y) / FLY_SPEED;
        float angVel = spinDir * (spinBase + spinAmp * Mathf.Sin(spinT * spinFreq));
        float predicted = Norm(-90f - (logAngle + angVel * flight));
        float nearest = 999f;
        for (int i = 0; i < occupied.Count; i++) nearest = Mathf.Min(nearest, AngDiff(predicted, occupied[i]));
        if (nearest > COLLIDE_DEG * 2.0f) { autoCooldown = Random.Range(0.22f, 0.5f); return true; }
        return false;
    }

    // ===================================================================== HUD
    TextMesh MakeText(float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = 96; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(camT, false);
        t.transform.localRotation = Quaternion.identity;
        return t;
    }

    void BuildHud()
    {
        hudStage  = MakeText(0.072f, Color.white, TextAnchor.UpperLeft);
        hudScore  = MakeText(0.052f, new Color(1f, 0.88f, 0.4f), TextAnchor.UpperLeft);
        hudBest   = MakeText(0.052f, new Color(0.8f, 0.92f, 1f), TextAnchor.UpperRight);
        hudKnives = MakeText(0.060f, new Color(0.92f, 0.96f, 1f), TextAnchor.LowerCenter);
        comboText = MakeText(0.075f, new Color(1f, 0.6f, 0.3f), TextAnchor.MiddleCenter);
        bannerText= MakeText(0.11f, Color.white, TextAnchor.MiddleCenter);
        dbg       = MakeText(0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        comboText.text = ""; bannerText.text = "";
        AdjustHud();
    }

    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 5.4f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.2f, 1.3f);
        float ix = halfW * 0.92f, iy = halfH * 0.92f;

        hudStage.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudStage.characterSize = 0.072f * hudScale;
        hudScore.transform.localPosition = new Vector3(-ix, iy - 0.62f * hudScale, HUD_Z); hudScore.characterSize = 0.052f * hudScale;
        hudBest.transform.localPosition = new Vector3(ix, iy, HUD_Z); hudBest.characterSize = 0.052f * hudScale;
        hudKnives.transform.localPosition = new Vector3(0, -iy, HUD_Z); hudKnives.characterSize = 0.060f * hudScale;
        comboText.transform.localPosition = new Vector3(0, halfH * 0.42f, HUD_Z);
        if (comboFlash <= 0f) comboText.characterSize = 0.075f * hudScale;
        else comboText.characterSize = 0.075f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.4f);
        dbg.transform.localPosition = new Vector3(-ix, -iy * 0.4f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
    }

    void RefreshHud()
    {
        if (hudStage) hudStage.text = (isBoss ? "BOSS " : "STAGE ") + stage;
        if (hudScore) hudScore.text = "SCORE " + score + "    COINS " + coins;
        if (hudBest) hudBest.text = "BEST " + best + (bestCombo > 1 ? "\nSPLIT x" + bestCombo : "");
        if (hudKnives)
        {
            int left = Mathf.Max(0, knivesToLand - knivesLanded);
            string s = "";
            for (int i = 0; i < left; i++) s += "I ";
            hudKnives.text = "KNIVES  " + left + "\n" + s;
        }
    }

    // ===================================================================== banners
    float bannerTimer;
    void Banner(string s, Color c, float dur)
    {
        bannerText.transform.localPosition = new Vector3(0f, halfH * 0.52f, HUD_Z);
        bannerText.characterSize = 0.085f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = dur;
    }

    void TickBanner(float dt)
    {
        if (bannerTimer > 0f && bannerTimer < 900f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
    }

    // ===================================================================== helpers
    static float Norm(float deg) { deg %= 360f; if (deg < 0f) deg += 360f; return deg; }
    static float AngDiff(float a, float b)
    {
        float d = Mathf.Abs(Norm(a) - Norm(b)) % 360f;
        return d > 180f ? 360f - d : d;
    }

    void UpdateDbg(float angVel)
    {
        float a = ContactLocalAngle();
        float nearest = 999f;
        for (int i = 0; i < occupied.Count; i++) nearest = Mathf.Min(nearest, AngDiff(a, occupied[i]));
        dbg.text = string.Format(
            "stage {0} boss {1}  state {2}/{3}\nlogAng {4:0} angVel {5:0}\ncontact {6:0.0} nearest {7:0.0}\nland {8}/{9} occ {10} apples {11}\ncombo {12} score {13} fps {14:0}",
            stage, isBoss, state, thr, Norm(logAngle), angVel, a, nearest,
            knivesLanded, knivesToLand, occupied.Count, ActiveApples(), combo, score,
            1f / Mathf.Max(0.0001f, Time.smoothDeltaTime));
    }
    int ActiveApples() { int n = 0; foreach (var a in apples) if (a.alive) n++; return n; }
}
