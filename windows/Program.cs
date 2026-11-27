// WinterIsComing for Windows — snow on your desktop.
// Snow piles up on top of windows and on the taskbar, and flies off when you drag a window.
// Roll snowballs by sliding the mouse over the snow, push them, carry them, stack three into a snowman,
// and hang fairy lights on your windows.
// Pure Win32 + a tiny software renderer: no WinForms, no extra packages.
//
// Run:     dotnet run -c Release
// Build:   dotnet publish -c Release -r win-x64   (or win-arm64) --self-contained -p:PublishSingleFile=true
// A ❄ icon appears in the system tray (bottom right): pause / snow amount / fairy lights / clear / how to play / quit.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinterIsComing;

// MARK: - Settings (feel free to tweak)

static class Cfg
{
    public static int TargetFlakes = 260;          // number of flakes falling at the same time
    public const float ColW = 3;                   // width of one column of the snow height map
    public const float WindowPileMax = 16;         // max snow height on top of a window
    public const float GroundPileMax = 34;         // max snow height at the bottom of the screen
    public const float TaskbarPileMax = 18;        // max snow height on the taskbar
    public const float TaskbarGain = 2.0f;         // snow on the taskbar builds up this much faster
    public const float Gravity = 900;              // gravity for snow sliding off
    public const float PollInterval = 1f / 30f;    // how often window positions are checked (s)
    public const int MaxClumps = 900;              // max number of falling snow clumps at once
    public const float FallSpeed = 1.5f;           // falling speed multiplier
    public const bool FlakesInFront = true;        // some of the smallest flakes fall in front of windows
    public const float InFrontFraction = 0.4f;     // share of the smallest layer that falls in front
    public const int FrameMs = 16;                 // timer interval (~60 fps)
    public const float BallStartRadius = 3;        // a new snowball starts this small
    public const float BallMaxRadius = 28;         // and stops growing at this size
    public const int MaxBalls = 20;                // oldest resting snowballs are removed beyond this
    public const float LightSpacing = 22;          // distance between fairy light bulbs
    public const float LightSpan = 120;            // distance between the points where the wire is "hooked"
    public const float LightSag = 9;               // how far the wire droops between hooks
    public const float LightInset = 14;            // keep clear of the window's rounded corners
    public const float BulbGlow = 19;              // size of a bulb's soft glow
    public const float BulbCore = 5.5f;            // size of a bulb's bright center
}

// Three depth layers: far = small, faint, slow; near = big, bright, fast
readonly record struct Layer(float SizeMin, float SizeMax, float SpeedMin, float SpeedMax,
                             float AlphaMin, float AlphaMax, float SwayMin, float SwayMax, float Wind);

static class Rnd
{
    static readonly Random R = new();
    public static float F(float a, float b) => a + (float)R.NextDouble() * (b - a);
}

// MARK: - Flake

sealed class Flake
{
    public float X, Y, Vx, Vy;
    public float Size = 4, Alpha = 1;
    public float SwayAmp, SwayFreq = 1, Phase, WindFactor = 1;
    public int Layer;                // 0 far, 1 middle, 2 near
    public bool Clump;               // sliding off a surface (affected by gravity)
    public bool InFront;             // falls in front of windows
    public long IgnoreId = -1;       // don't land right back on the surface it came from
    public float IgnoreUntil;
}

// MARK: - Snow pile (a height map drawn as a smooth white mound). y grows downwards.

sealed class Pile
{
    public float MinX, TopY;          // TopY = the edge the snow sits on
    public readonly float Width, MaxH;
    public readonly float[] H, Caps;  // current height / max height per column
    public readonly bool[] Occ;       // column hidden behind a window in front
    public bool Dirty;

    public Pile(float minX, float topY, float width, float maxH)
    {
        MinX = minX; TopY = topY; Width = width; MaxH = maxH;
        int n = Math.Max(1, (int)MathF.Ceiling(width / Cfg.ColW));
        H = new float[n]; Caps = new float[n]; Occ = new bool[n];
        for (int i = 0; i < n; i++)
        {
            float edgeDist = MathF.Min(i + 0.5f, n - i - 0.5f) * Cfg.ColW;
            float t = MathF.Min(1, edgeDist / (maxH * 2.2f));
            Caps[i] = maxH * MathF.Sqrt(t);   // taper at both ends so it looks rounded
        }
    }

    public int Count => H.Length;
    public float Total { get { float s = 0; foreach (var h in H) s += h; return s; } }
    public int Col(float x) { int c = (int)MathF.Floor((x - MinX) / Cfg.ColW); return c >= 0 && c < Count ? c : -1; }
    public float ColX(int c) => MinX + (c + 0.5f) * Cfg.ColW;
    public float Surface(int c) => TopY - H[c];

    static readonly (int, float)[] Spread = { (0, 1), (-1, .75f), (1, .75f), (-2, .45f), (2, .45f), (-3, .2f), (3, .2f) };

    public void Add(int c, float amount)
    {
        foreach (var (o, w) in Spread)
        {
            int i = c + o;
            if (i < 0 || i >= Count || Occ[i]) continue;
            H[i] = MathF.Min(Caps[i], H[i] + amount * w);
        }
        Dirty = true;
    }

    // Let snow slump sideways where the slope is too steep
    public void Relax()
    {
        if (!Dirty) return;
        bool moved = false;
        for (int i = 0; i < Count - 1; i++)
        {
            if (Occ[i] || Occ[i + 1]) continue;
            float d = H[i] - H[i + 1];
            if (MathF.Abs(d) > 1f)
            {
                float m = (MathF.Abs(d) - 1f) * 0.3f * MathF.Sign(d);
                H[i] = Math.Clamp(H[i] - m, 0, Caps[i]);
                H[i + 1] = Math.Clamp(H[i + 1] + m, 0, Caps[i + 1]);
                if (MathF.Abs(m) > 0.02f) moved = true;
            }
        }
        Dirty = moved;
    }

    public void Clear() { Array.Clear(H); Dirty = true; }
}

record struct Win(long Id, float L, float T, float R, float B)
{
    public float Width => R - L;
    public bool Contains(float x, float y) => x >= L && x < R && y >= T && y < B;
}

// MARK: - Snowball

// A little grey-blue speck on the ball's surface; specks turn with the ball so you can see it roll
readonly record struct Speck(float Ang, float Dist, float Size, float Alpha);

sealed class Snowball
{
    public float X, Y, R = Cfg.BallStartRadius;   // center (y down) and radius
    public float Vx, Vy, Rot;
    public Pile Pile;              // the surface it rests on (null while falling, dragged or stacked)
    public Snowball Below;         // the ball it is stacked on (snowman), if any
    public bool Falling, Dragging, Hidden;
    public int Decor;              // 0 = plain, 1 = snowman head, 2 = snowman body
    public readonly Speck[] Specks;

    public Snowball()
    {
        Rot = Rnd.F(0, 6.28f);
        var list = new List<Speck>();
        while (list.Count < 18)
        {
            float d = MathF.Sqrt(Rnd.F(0, 1)) * 0.86f, size = Rnd.F(0.025f, 0.06f);
            if (d + size < 0.9f) list.Add(new Speck(Rnd.F(0, 6.28f), d, size, Rnd.F(0.25f, 0.55f)));
        }
        Specks = list.ToArray();
    }

    public void Drop(float vx, float vy)
    {
        Falling = true;
        Dragging = false;
        Pile = null;
        Below = null;
        Vx = vx; Vy = vy;
    }
}

// MARK: - Fairy lights

sealed class Bulb
{
    public readonly float Phase = Rnd.F(0, 6.28f), Speed = Rnd.F(1.2f, 3.0f);
    public int Color;
    public float X, Y;
    public bool Hidden;
}

/// A string of fairy lights hanging just below the top edge of one window
sealed class LightString
{
    public static readonly (float r, float g, float b)[] Colors =
    {
        (1.00f, 0.10f, 0.12f),   // red
        (1.00f, 0.62f, 0.00f),   // amber
        (0.05f, 0.95f, 0.20f),   // green
        (0.10f, 0.42f, 1.00f),   // blue
        (1.00f, 0.15f, 0.62f),   // pink
    };
    public const float SwingTime = 1.4f;

    public Win Rect;
    public float HungAt;         // when this string was hung up
    public float Swing, Sway;    // extra droop while it bounces / sideways swing of the bulbs
    public readonly List<Bulb> Bulbs = new();
    public readonly List<(float x, float y, bool hidden)> Wire = new();

    public bool Animating(float t) => t - HungAt < SwingTime;

    /// Just after being hung, the string bounces and the bulbs swing, then settle down (a damped wobble)
    public void UpdateSwing(float t)
    {
        float tau = t - HungAt;
        if (tau >= SwingTime) { Swing = 0; Sway = 0; return; }
        float e = MathF.Exp(-4.0f * tau);
        Swing = 0.9f * e * MathF.Sin(13 * tau);
        Sway = 3.5f * e * MathF.Sin(9.5f * tau);
    }

    /// Bulbs light up one after another from left to right as the string is hung
    public float Reveal(int i, float t) => Math.Clamp((t - HungAt - i * 0.015f) / 0.08f, 0, 1);

    /// Height of the wire at x: it droops between hooks like a real string of lights
    public float WireY(float x)
    {
        float x0 = Rect.L + Cfg.LightInset;
        float w = MathF.Max(1, Rect.Width - 2 * Cfg.LightInset);
        float span = w / MathF.Max(1, MathF.Round(w / Cfg.LightSpan, MidpointRounding.AwayFromZero));
        float f = Math.Clamp(((x - x0) / span) % 1, 0, 1);
        return Rect.T + 3 + Cfg.LightSag * (1 + Swing) * MathF.Sin(MathF.PI * f);
    }

    /// Follow the window; new bulbs are only made when its width changes
    public void Place(Win r, Func<float, float, bool> hidden)
    {
        Rect = r;
        float w = MathF.Max(0, r.Width - 2 * Cfg.LightInset);
        int count = Math.Max(2, (int)(w / Cfg.LightSpacing));
        if (Bulbs.Count != count)
        {
            Bulbs.Clear();
            for (int i = 0; i < count; i++) Bulbs.Add(new Bulb { Color = i % Colors.Length });
        }
        for (int i = 0; i < count; i++)
        {
            var b = Bulbs[i];
            float x = r.L + Cfg.LightInset + (i + 0.5f) * w / count;
            float dx = Sway * (0.6f + 0.4f * MathF.Sin(b.Phase));   // each bulb swings a little differently
            b.X = x + dx;
            b.Y = WireY(x) + 4;                                      // bulbs hang just under the wire
            b.Hidden = hidden(b.X, b.Y);
        }
        Wire.Clear();
        for (float x = r.L + Cfg.LightInset * 0.5f; x <= r.R - Cfg.LightInset * 0.5f; x += 4)
        {
            float y = WireY(x);
            Wire.Add((x, y, hidden(x, y)));
        }
    }
}

// MARK: - The snow world (all coordinates in logical pixels, y down)

sealed class SnowWorld
{
    static readonly Layer[] Layers =
    {
        new(1.8f, 3.0f, 22, 38,  0.30f, 0.50f, 5, 10,  0.5f),
        new(3.0f, 4.8f, 40, 65,  0.60f, 0.80f, 9, 16,  0.8f),
        new(4.8f, 7.5f, 70, 105, 0.85f, 1.00f, 14, 24, 1.1f),
    };

    public float W, H;
    public readonly List<Flake> Flakes = new();
    readonly Stack<Flake> pool = new();
    public readonly Dictionary<long, Pile> Piles = new();
    readonly Dictionary<long, Win> lastRect = new();
    public List<Win> Order = new();              // windows from front to back
    public Pile Ground;
    public bool GroundIsTaskbar;
    float pollAcc, t;
    const long GroundId = -2, BurstId = -3;

    public readonly List<Snowball> Balls = new();
    public readonly Dictionary<long, LightString> Lights = new();
    public bool LightsOn;
    Snowball activeBall, dragBall;
    float dragTx, dragTy, dragOx, dragOy, dragVx, dragVy;
    float lastMx = float.NaN;

    public SnowWorld(float w, float h)
    {
        W = w; H = h;
        ResetGround(h, false);
        for (int i = 0; i < Cfg.TargetFlakes; i++) SpawnFlake(Rnd.F(0, H));
    }

    public void Resize(float w, float h)
    {
        W = w; H = h;
        ResetGround(h, false);
    }

    void ResetGround(float top, bool taskbar)
    {
        foreach (var b in Balls) if (Ground != null && b.Pile == Ground) b.Drop(0, 0);
        if (Ground != null) Release(Ground, 0, 0, GroundId);
        GroundIsTaskbar = taskbar;
        Ground = new Pile(0, top, W, taskbar ? Cfg.TaskbarPileMax : Cfg.GroundPileMax);
    }

    public void ClearAll()
    {
        foreach (var p in Piles.Values) p.Clear();
        Ground.Clear();
        Balls.Clear();
        activeBall = dragBall = null;
    }

    // MARK: Flake pool

    Flake Obtain()
    {
        var f = pool.Count > 0 ? pool.Pop() : new Flake();
        Flakes.Add(f);
        return f;
    }

    void Recycle(int i)
    {
        pool.Push(Flakes[i]);
        Flakes[i] = Flakes[^1];
        Flakes.RemoveAt(Flakes.Count - 1);
    }

    void SpawnFlake(float? y = null)
    {
        var f = Obtain();
        float r = Rnd.F(0, 1);
        int li = r < 0.40f ? 0 : (r < 0.68f ? 1 : 2);   // 40% far, 28% middle, 32% near
        var L = Layers[li];
        f.Layer = li;
        f.Clump = false;
        f.InFront = Cfg.FlakesInFront && li == 0 && Rnd.F(0, 1) < Cfg.InFrontFraction;
        f.IgnoreId = -1;
        f.Size = Rnd.F(L.SizeMin, L.SizeMax);
        f.Vx = 0;
        f.Vy = Rnd.F(L.SpeedMin, L.SpeedMax) * Cfg.FallSpeed;
        f.SwayAmp = Rnd.F(L.SwayMin, L.SwayMax);
        f.SwayFreq = Rnd.F(0.6f, 1.6f);
        f.Phase = Rnd.F(0, 6.28f);
        f.WindFactor = L.Wind;
        f.Alpha = Rnd.F(L.AlphaMin, L.AlphaMax);
        f.X = Rnd.F(-40, W + 40);
        f.Y = y ?? -Rnd.F(2, 30);
    }

    void SpawnClump(float x, float y, float vx, float vy, long sourceId)
    {
        var f = Obtain();
        f.Clump = true;
        f.InFront = false;
        f.Layer = 2;
        f.Size = Rnd.F(2.5f, 5);
        f.Alpha = 0.95f;
        f.Vx = vx; f.Vy = vy;
        f.IgnoreId = sourceId;
        f.IgnoreUntil = t + 0.35f;
        f.X = x; f.Y = y;
    }

    // Turn a pile into falling clumps; (vx, vy) is the window's velocity, so faster drags fling snow farther
    void Release(Pile p, float vx, float vy, long id)
    {
        float fx = Math.Clamp(vx * 0.5f, -900, 900);
        float fy = Math.Clamp(vy * 0.4f, -500, 300);
        // Snowballs sitting on this surface get flung off too
        foreach (var b in Balls) if (b.Pile == p) b.Drop(fx + Rnd.F(-30, 30), fy - Rnd.F(20, 80));
        float total = p.Total;
        if (total > 0.5f)
        {
            float budget = Math.Max(0, Cfg.MaxClumps - Flakes.Count + Cfg.TargetFlakes);
            float k = MathF.Min(1, budget / MathF.Max(1, total / 3.5f));
            for (int c = 0; c < p.Count; c++)
            {
                if (p.H[c] <= 0.6f) continue;
                float amount = p.H[c] / 3.5f * k;
                while (amount > 0)
                {
                    if (amount < 1 && Rnd.F(0, 1) > amount) break;
                    SpawnClump(p.ColX(c) + Rnd.F(-1.5f, 1.5f), p.TopY - Rnd.F(0, p.H[c]),
                               fx + Rnd.F(-40, 40), fy - Rnd.F(10, 80), id);
                    amount -= 1;
                }
            }
        }
        p.Clear();
    }

    // MARK: Per-frame update

    public float Time => t;

    /// (mx, my) = mouse position in logical pixels
    public void Update(float dt, Func<List<Win>> readWindows, Func<float?> readTaskbarTop, float mx, float my)
    {
        t += dt;
        pollAcc += dt;
        if (pollAcc >= Cfg.PollInterval)
        {
            PollWindows(pollAcc, readWindows(), readTaskbarTop());
            pollAcc = 0;
        }
        StepFlakes(dt);
        if (dragBall != null) StepDrag(dragBall, dt, mx, my); else UpdateRolling(mx, my);
        StepBalls(dt);
        UpdateLights();
    }

    void PollWindows(float dt, List<Win> wins, float? taskbarTop)
    {
        // Taskbar at the bottom: the snow on the "ground" sits on top of it
        float groundTop = taskbarTop ?? H;
        bool taskbar = taskbarTop.HasValue;
        if (MathF.Abs(groundTop - Ground.TopY) > 1 || taskbar != GroundIsTaskbar) ResetGround(groundTop, taskbar);

        // Closed / minimized windows: their snow just falls
        var alive = new HashSet<long>();
        foreach (var w in wins) alive.Add(w.Id);
        var gone = new List<long>();
        foreach (var kv in Piles) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
        foreach (var id in gone)
        {
            Release(Piles[id], 0, 0, id);
            Piles.Remove(id);
            lastRect.Remove(id);
        }

        foreach (var r in wins)
        {
            if (Piles.TryGetValue(r.Id, out var pile) && lastRect.TryGetValue(r.Id, out var old))
            {
                if (MathF.Abs(old.Width - r.Width) > 0.5f)
                {
                    // Width changed: drop the snow and start a new pile
                    Release(pile, 0, 0, r.Id);
                    Piles[r.Id] = new Pile(r.L, r.T, r.Width, Cfg.WindowPileMax);
                }
                else if (MathF.Abs(old.L - r.L) > 0.3f || MathF.Abs(old.T - r.T) > 0.3f)
                {
                    // Window dragged: fling the snow off
                    float vx = (r.L - old.L) / dt, vy = (r.T - old.T) / dt;
                    pile.MinX = r.L;
                    pile.TopY = r.T;
                    Release(pile, vx, vy, r.Id);
                }
            }
            else
            {
                Piles[r.Id] = new Pile(r.L, r.T, r.Width, Cfg.WindowPileMax);
            }
            lastRect[r.Id] = r;
        }
        Order = wins;

        ComputeOcclusion();
        foreach (var p in Piles.Values) p.Relax();
        Ground.Relax();
    }

    // Parts of a window top hidden behind a front window get no snow and aren't drawn
    void ComputeOcclusion()
    {
        for (int i = 0; i < Order.Count; i++)
        {
            if (!Piles.TryGetValue(Order[i].Id, out var p)) continue;
            for (int c = 0; c < p.Count; c++)
            {
                float x = p.ColX(c), y = p.TopY - 1;
                bool o = x < 0 || x > W || y < 0;
                if (!o) for (int j = 0; j < i; j++) if (Order[j].Contains(x, y)) { o = true; break; }
                if (o != p.Occ[c]) { p.Occ[c] = o; if (o) p.H[c] = 0; p.Dirty = true; }
            }
        }
        // The taskbar is always in front of windows; the plain screen bottom is not
        for (int c = 0; c < Ground.Count; c++)
        {
            bool o = false;
            if (!GroundIsTaskbar)
            {
                float x = Ground.ColX(c), y = Ground.TopY - 1;
                foreach (var w in Order) if (w.Contains(x, y)) { o = true; break; }
            }
            if (o != Ground.Occ[c]) { Ground.Occ[c] = o; if (o) Ground.H[c] = 0; Ground.Dirty = true; }
        }
    }

    void StepFlakes(float dt)
    {
        // A few sines at different frequencies add up to gusty wind
        float wind = 14 * MathF.Sin(t * 0.11f) + 9 * MathF.Sin(t * 0.37f + 1.3f) + 6 * MathF.Sin(t * 0.9f + 0.4f);
        int normal = 0;
        for (int i = Flakes.Count - 1; i >= 0; i--)
        {
            var f = Flakes[i];
            float prevY = f.Y;
            if (f.Clump)
            {
                f.Vy += Cfg.Gravity * dt;
                f.Vx *= MathF.Max(0, 1 - 1.2f * dt);
                f.X += f.Vx * dt;
                f.Y += f.Vy * dt;
            }
            else
            {
                float sway = MathF.Sin(t * f.SwayFreq + f.Phase) * f.SwayAmp;
                f.X += (wind * f.WindFactor + sway) * dt;
                f.Y += f.Vy * dt;
                if (f.X < -40) f.X += W + 80; else if (f.X > W + 40) f.X -= W + 80;
            }

            bool gone = false;
            if (f.Y > prevY)
            {
                // Window tops, front to back (flakes falling in front of windows skip them)
                if (!f.InFront)
                {
                    foreach (var w in Order)
                    {
                        if (!Piles.TryGetValue(w.Id, out var pile)) continue;
                        if (f.Clump && w.Id == f.IgnoreId && t < f.IgnoreUntil) continue;
                        int c = pile.Col(f.X);
                        if (c < 0 || pile.Occ[c]) continue;
                        float s = pile.Surface(c);
                        if (prevY <= s && f.Y > s)
                        {
                            pile.Add(c, f.Clump ? 1.6f : f.Size * 0.16f);
                            gone = true;
                            break;
                        }
                    }
                }
                // Then the taskbar / bottom of the screen
                if (!gone)
                {
                    int c = Ground.Col(f.X);
                    if (c >= 0 && f.Y > Ground.Surface(c))
                    {
                        if (!Ground.Occ[c])
                        {
                            float gain = GroundIsTaskbar ? Cfg.TaskbarGain : 1;
                            Ground.Add(c, (f.Clump ? 1.6f : f.Size * 0.12f) * gain);
                        }
                        gone = true;
                    }
                }
            }
            if (!gone)
            {
                // Flakes that fall "behind" a window disappear
                if (!f.InFront) foreach (var w in Order) if (w.Contains(f.X, f.Y)) { gone = true; break; }
                if (f.Y > H + 20) gone = true;
                if (f.Clump && (f.X < -80 || f.X > W + 80)) gone = true;
            }

            if (gone) Recycle(i);
            else if (!f.Clump) normal++;
        }

        // Top up flakes (max 6 per frame so they don't appear in a row)
        int deficit = Math.Min(6, Cfg.TargetFlakes - normal);
        for (int k = 0; k < deficit; k++) SpawnFlake();
    }

    // MARK: Fairy lights

    /// Hang a string of lights on every window, following it when it moves
    void UpdateLights()
    {
        if (!LightsOn) { Lights.Clear(); return; }
        var alive = new HashSet<long>();
        for (int i = 0; i < Order.Count; i++)
        {
            var w = Order[i];
            alive.Add(w.Id);
            if (!Lights.TryGetValue(w.Id, out var s))
            {
                s = new LightString { HungAt = t };
                Lights[w.Id] = s;
            }
            s.UpdateSwing(t);
            int front = i;
            s.Place(w, (x, y) =>
            {
                if (y < 0) return true;
                for (int j = 0; j < front; j++) if (Order[j].Contains(x, y)) return true;
                return false;
            });
        }
        var dead = new List<long>();
        foreach (var id in Lights.Keys) if (!alive.Contains(id)) dead.Add(id);
        foreach (var id in dead) Lights.Remove(id);
    }

    // MARK: Grabbing & snowmen

    /// The overlay normally lets every click through. Only while the mouse is over a snowball (or dragging one)
    /// does it catch clicks, so you can grab the ball without clicking the window underneath.
    public bool WantsMouse(float mx, float my) => dragBall != null || BallAt(mx, my) != null;

    /// The visible, resting snowball under a point (top of a stack first)
    Snowball BallAt(float mx, float my)
    {
        Snowball best = null;
        foreach (var b in Balls)
        {
            if (b.Falling || b.Hidden) continue;
            float dx = mx - b.X, dy = my - b.Y;
            if (dx * dx + dy * dy <= (b.R + 2) * (b.R + 2) && (best == null || b.Y < best.Y)) best = b;
        }
        return best;
    }

    Snowball Above(Snowball b) { foreach (var x in Balls) if (x.Below == b) return x; return null; }

    static Snowball BaseOf(Snowball b) { while (b.Below != null) b = b.Below; return b; }

    /// Balls in a stack from the bottom up
    List<Snowball> Stack(Snowball bottom)
    {
        var list = new List<Snowball> { bottom };
        for (var a = Above(bottom); a != null && list.Count < 10; a = Above(a)) list.Add(a);
        return list;
    }

    public bool MouseDown(float mx, float my)
    {
        var b = BallAt(mx, my);
        if (b == null) return false;
        // Anything stacked on top of the grabbed ball tumbles off
        for (var a = Above(b); a != null;)
        {
            var next = Above(a);
            a.Drop(Rnd.F(-50, 50), -Rnd.F(40, 100));
            a = next;
        }
        foreach (var x in Stack(BaseOf(b))) x.Decor = 0;
        b.Pile = null;
        b.Below = null;
        b.Dragging = true;
        dragBall = b;
        activeBall = null;
        dragOx = b.X - mx; dragOy = b.Y - my;
        dragTx = mx; dragTy = my;
        dragVx = dragVy = 0;
        return true;
    }

    public void MouseUp()
    {
        var b = dragBall;
        if (b == null) return;
        dragBall = null;
        b.Dragging = false;
        var target = StackTarget(b);
        if (target != null)
        {
            b.Below = target;
            b.Vx = b.Vy = 0;
            var st = Stack(BaseOf(b));
            if (st.Count == 3) { st[1].Decor = 2; st[2].Decor = 1; }   // three stacked balls become a snowman
        }
        else
        {
            // Let go: it falls (with a bit of the throw) and lands on whatever is below
            b.Drop(Math.Clamp(dragVx * 0.5f, -600, 600), Math.Clamp(dragVy * 0.5f, -400, 400));
        }
    }

    void StepDrag(Snowball b, float dt, float mx, float my)
    {
        dragTx = mx; dragTy = my;
        float tx = dragTx + dragOx, ty = dragTy + dragOy;
        if (dt > 0)
        {
            dragVx = dragVx * 0.7f + (tx - b.X) / dt * 0.3f;
            dragVy = dragVy * 0.7f + (ty - b.Y) / dt * 0.3f;
        }
        b.Rot += (tx - b.X) / b.R;
        b.X = tx; b.Y = ty;
        lastMx = mx;
    }

    /// A ball dropped just above another ball (that isn't already a full snowman) gets stacked on it
    Snowball StackTarget(Snowball b)
    {
        Snowball best = null;
        float bestDist = float.MaxValue;
        foreach (var c in Balls)
        {
            if (c == b || c.Falling || c.Dragging || c.Hidden) continue;
            if (Above(c) != null || Stack(BaseOf(c)).Count >= 3) continue;
            float dx = MathF.Abs(b.X - c.X), dy = c.Y - b.Y;      // dy > 0: b is above c
            if (dx < c.R * 0.9f + b.R * 0.3f && dy > 0 && dy < c.R + b.R + 30 && dx < bestDist) { best = c; bestDist = dx; }
        }
        return best;
    }

    // MARK: Rolling snowballs

    /// Is the mouse inside the snow band of this pile (a bit below the edge up to a bit above the snow)?
    static bool MouseOver(Pile p, float mx, float my)
    {
        int c = p.Col(mx);
        if (c < 0 || p.Occ[c]) return false;
        return my <= p.TopY + 6 && my >= p.TopY - p.H[c] - 14;
    }

    /// Which snow surface is the mouse hovering over? Taskbar first, then windows front to back, then the ground.
    Pile SurfaceUnderMouse(float mx, float my)
    {
        // Hovering over a snowball counts as hovering over the snow it sits on, so pushing a big ball stays smooth
        foreach (var b in Balls)
        {
            if (b.Falling || b.Dragging || b.Hidden || b.Below != null || b.Pile == null) continue;
            var p = b.Pile;
            if (MathF.Abs(mx - b.X) < b.R + 8 && my <= p.TopY + 6 && my >= p.TopY - 2 * b.R - 8) return p;
        }
        if (GroundIsTaskbar)
        {
            if (MouseOver(Ground, mx, my)) return Ground;
            if (my >= Ground.TopY) return null;              // over the taskbar itself
        }
        foreach (var w in Order)
        {
            if (Piles.TryGetValue(w.Id, out var p) && MouseOver(p, mx, my)) return p;
            if (w.Contains(mx, my)) return null;             // over a window's content
        }
        if (!GroundIsTaskbar && MouseOver(Ground, mx, my)) return Ground;
        return null;
    }

    void UpdateRolling(float mx, float my)
    {
        float dx = float.IsNaN(lastMx) ? 0 : mx - lastMx;
        lastMx = mx;
        var p = SurfaceUnderMouse(mx, my);
        if (p == null) { activeBall = null; return; }
        if (MathF.Abs(dx) <= 0.2f) return;

        // Keep rolling the current ball, pick up a resting one under the mouse, or start a new one
        Snowball b = null;
        if (activeBall != null && activeBall.Pile == p && Above(activeBall) == null &&
            MathF.Abs(activeBall.X - (mx - dx)) < activeBall.R + 25)
        {
            b = activeBall;
        }
        else
        {
            Snowball resting = null;
            foreach (var x in Balls) if (!x.Falling && x.Pile == p && MathF.Abs(x.X - mx) < x.R + 10) { resting = x; break; }
            if (resting != null)
            {
                if (Above(resting) != null) { activeBall = null; return; }   // don't roll the bottom of a snowman
                b = resting;
            }
            else
            {
                int c = p.Col(mx);
                if (c < 0 || p.H[c] <= 1.5f) { activeBall = null; return; }
                b = new Snowball { X = mx, Pile = p };
                b.Y = p.TopY - b.R + 1.5f;
                Balls.Add(b);
                TrimBalls(b);
            }
        }
        activeBall = b;

        // Move the ball to the mouse, staying on the surface
        float lo = p.MinX + b.R, hi = p.MinX + p.Width - b.R;
        float newX = lo < hi ? Math.Clamp(mx, lo, hi) : b.X;
        float moved = newX - b.X;
        if (MathF.Abs(moved) <= 0.01f) return;
        float oldX = b.X;
        Roll(b, p, newX);
        Push(b, oldX, moved > 0 ? 1 : -1, p);
    }

    /// Roll a ball to newX: it picks up snow from the columns it passes over and grows
    void Roll(Snowball b, Pile p, float newX)
    {
        float moved = newX - b.X;
        if (MathF.Abs(moved) <= 0.01f) return;
        if (b.R < Cfg.BallMaxRadius)
        {
            float collected = 0;
            float from = MathF.Min(b.X, newX) - b.R * 0.8f;
            float to = MathF.Max(b.X, newX) + b.R * 0.8f;
            float frac = MathF.Min(0.6f, 0.6f * MathF.Abs(moved) / (1.6f * b.R));
            int c0 = p.Col(MathF.Max(from, p.MinX)), c1 = p.Col(MathF.Min(to, p.MinX + p.Width - 0.1f));
            if (c0 >= 0 && c1 >= 0)
            {
                for (int c = c0; c <= c1; c++)
                {
                    if (p.Occ[c]) continue;
                    float take = p.H[c] * frac;
                    p.H[c] -= take;
                    collected += take;
                }
                p.Dirty = true;
            }
            // Ball area grows by the snow area it picked up
            float area = MathF.PI * b.R * b.R + collected * Cfg.ColW * 0.8f;
            b.R = MathF.Min(Cfg.BallMaxRadius, MathF.Sqrt(area / MathF.PI));
        }
        b.X = newX;
        b.Rot += moved / b.R;
    }

    /// The moving ball pushes any ball it touches in front of it; that ball can push the next one, and so on.
    /// A ball pushed past the end of a surface falls off.
    void Push(Snowball b, float oldX, float dir, Pile p)
    {
        var ahead = new List<Snowball>();
        foreach (var o in Balls)
            if (o != b && !o.Falling && !o.Dragging && o.Pile == p && (o.X - oldX) * dir > 0) ahead.Add(o);
        ahead.Sort((a, c) => MathF.Abs(a.X - oldX).CompareTo(MathF.Abs(c.X - oldX)));
        var pusher = b;
        foreach (var o in ahead)
        {
            float need = pusher.X + dir * (pusher.R + o.R);   // where o must be to just touch the pusher
            if ((need - o.X) * dir <= 0) break;              // not touching: the chain stops here
            float lo = p.MinX + o.R, hi = p.MinX + p.Width - o.R;
            if (need < lo || need > hi)
            {
                o.X = need;
                o.Drop(dir * 140, -40);                      // pushed off the edge
            }
            else Roll(o, p, need);
            pusher = o;
        }
    }

    void TrimBalls(Snowball keep)
    {
        while (Balls.Count > Cfg.MaxBalls)
        {
            int i = Balls.FindIndex(x => !x.Falling && !x.Dragging && x.Decor == 0 && x != keep && x != activeBall);
            if (i < 0) break;
            Balls.RemoveAt(i);
        }
    }

    void StepBalls(float dt)
    {
        for (int i = Balls.Count - 1; i >= 0; i--)
        {
            var b = Balls[i];
            bool gone = false;
            if (b.Falling)
            {
                b.Decor = 0;
                b.Hidden = false;
                float prevBottom = b.Y + b.R;
                b.Vy += Cfg.Gravity * dt;
                b.X += b.Vx * dt;
                b.Y += b.Vy * dt;
                b.Rot += b.Vx * dt / b.R;
                var p = b.Vy > 0 ? LandingSurface(b.X, prevBottom, b.Y + b.R) : null;
                if (p != null) { Splat(b, p); gone = true; }   // falling snowballs burst into a heap of snow
                else if (b.Y > H + b.R * 2 || b.X < -100 || b.X > W + 100) gone = true;
            }
            else if (b.Dragging)
            {
                // position is set by StepDrag
            }
            else if (b.Below != null)
            {
                var u = b.Below;
                if (u.Falling || u.Dragging || !Balls.Contains(u))
                    b.Drop(u.Vx + Rnd.F(-40, 40), MathF.Min(u.Vy, 0) - Rnd.F(30, 80));
                else
                {
                    b.X = u.X;
                    b.Y = u.Y - u.R - b.R * 0.82f;   // sits slightly sunk into the ball below
                    b.Hidden = u.Hidden;
                }
            }
            else if (b.Pile != null)
            {
                int c = b.Pile.Col(b.X);
                b.Hidden = c < 0 || b.Pile.Occ[c];   // hidden behind a window in front
                b.Y = b.Pile.TopY - b.R + 1.5f;      // sits slightly sunk into the snow
            }
            else b.Drop(0, 0);                       // lost its support

            if (gone)
            {
                if (b == activeBall) activeBall = null;
                Balls.RemoveAt(i);
            }
        }
    }

    /// First surface a falling ball hits between two heights (y down): window tops front to back,
    /// then the taskbar / bottom of the screen, which catches everything.
    Pile LandingSurface(float x, float top, float bottom)
    {
        foreach (var w in Order)
        {
            if (!Piles.TryGetValue(w.Id, out var p)) continue;
            int c = p.Col(x);
            if (c < 0 || p.Occ[c]) continue;
            float s = p.Surface(c);
            if (top <= s && bottom > s) return p;
        }
        int g = Ground.Col(x);
        if (g >= 0 && bottom > Ground.Surface(g)) return Ground;
        return null;
    }

    /// A falling ball bursts where it lands: its snow becomes a mound on that surface, plus a puff of powder
    void Splat(Snowball b, Pile p)
    {
        int c0 = p.Col(b.X);
        if (c0 >= 0)
        {
            float snow = MathF.PI * b.R * b.R * 0.7f / Cfg.ColW;   // ball area -> total column height
            int half = Math.Max(2, (int)(b.R * 1.4f / Cfg.ColW));
            float sum = 0;
            for (int o = -half; o <= half; o++) sum += 1 - MathF.Abs(o) / (half + 1f);
            for (int o = -half; o <= half; o++)
            {
                int i = c0 + o;
                if (i < 0 || i >= p.Count || p.Occ[i]) continue;
                float w = (1 - MathF.Abs(o) / (half + 1f)) / sum;
                p.H[i] = MathF.Min(p.Caps[i], p.H[i] + snow * w);
            }
            p.Dirty = true;
        }
        int n = Math.Min(14, (int)(b.R * 0.6f) + 2);
        for (int k = 0; k < n; k++)
            SpawnClump(b.X + Rnd.F(-b.R, b.R), b.Y + b.R * 0.5f - Rnd.F(0, b.R * 0.5f),
                       b.Vx * 0.2f + Rnd.F(-90, 90), -Rnd.F(60, 180), BurstId);
    }
}

// MARK: - Software renderer into a premultiplied BGRA buffer

unsafe static class Render
{
    static void Blend(uint* p, float a, float r, float g, float b)
    {
        if (a <= 0.003f) return;
        if (a > 1) a = 1;
        uint d = *p;
        float inv = 1 - a;
        uint nb = (uint)MathF.Min(255, b * a * 255 + (d & 0xFF) * inv + 0.5f);
        uint ng = (uint)MathF.Min(255, g * a * 255 + ((d >> 8) & 0xFF) * inv + 0.5f);
        uint nr = (uint)MathF.Min(255, r * a * 255 + ((d >> 16) & 0xFF) * inv + 0.5f);
        uint na = (uint)MathF.Min(255, a * 255 + (d >> 24) * inv + 0.5f);
        *p = (na << 24) | (nr << 16) | (ng << 8) | nb;
    }

    // Soft round flake: solid center, fading edge
    public static void Disc(uint* buf, int bw, int bh, float cx, float cy, float radius, float alpha)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(cx - radius)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - radius)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + radius));
        float inv = 1 / radius;
        for (int y = y0; y <= y1; y++)
        {
            float dy = (y + 0.5f - cy) * inv;
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) * inv;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= 1) continue;
                float a = d < 0.45f ? 1 - 0.4f * (d / 0.45f) : 0.6f * (1 - (d - 0.45f) / 0.55f);
                Blend(row + x, a * alpha, 1, 1, 1);
            }
        }
    }

    // A pile: white mound above its edge with a thin blue-grey shadow just below it
    public static void Pile(uint* buf, int bw, int bh, Pile p, float s)
    {
        int px0 = Math.Max(0, (int)MathF.Floor(p.MinX * s)), px1 = Math.Min(bw - 1, (int)MathF.Ceiling((p.MinX + p.Width) * s));
        float baseY = p.TopY * s;
        for (int px = px0; px <= px1; px++)
        {
            // Interpolate the height between column centres for a smooth outline
            float u = ((px + 0.5f) / s - p.MinX) / Cfg.ColW - 0.5f;
            int c0 = (int)MathF.Floor(u);
            float fr = u - c0;
            int ca = Math.Clamp(c0, 0, p.Count - 1), cb = Math.Clamp(c0 + 1, 0, p.Count - 1);
            if (p.Occ[fr < 0.5f ? ca : cb]) continue;
            float h = (p.H[ca] * (1 - fr) + p.H[cb] * fr) * s;
            if (h < 0.3f * s) continue;

            float top = baseY - h;
            // shadow: 1.5 logical px below the edge
            float shEnd = baseY + 1.5f * s;
            for (int y = Math.Max(0, (int)MathF.Floor(baseY)); y < Math.Min(bh, (int)MathF.Ceiling(shEnd)); y++)
            {
                float cov = MathF.Min(1, shEnd - y);
                Blend(buf + (long)y * bw + px, 0.9f * cov, 0.70f, 0.78f, 0.92f);
            }
            // body
            int yTop = (int)MathF.Floor(top);
            for (int y = Math.Max(0, yTop); y < Math.Min(bh, (int)MathF.Ceiling(baseY)); y++)
            {
                float cov = y == yTop ? 1 - (top - yTop) : 1;   // anti-aliased top edge
                Blend(buf + (long)y * bw + px, 0.97f * cov, 1, 1, 1);
            }
        }
    }


    // Soft round glow in any color (same profile as a flake)
    public static void Glow(uint* buf, int bw, int bh, float cx, float cy, float radius, float alpha, float r, float g, float b)
    {
        if (alpha <= 0.003f || radius <= 0) return;
        int x0 = Math.Max(0, (int)MathF.Floor(cx - radius)), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + radius));
        int y0 = Math.Max(0, (int)MathF.Floor(cy - radius)), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + radius));
        float inv = 1 / radius;
        for (int y = y0; y <= y1; y++)
        {
            float dy = (y + 0.5f - cy) * inv;
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float dx = (x + 0.5f - cx) * inv;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= 1) continue;
                float a = d < 0.45f ? 1 - 0.4f * (d / 0.45f) : 0.6f * (1 - (d - 0.45f) / 0.55f);
                Blend(row + x, a * alpha, r, g, b);
            }
        }
    }

    // Fill any shape given by a signed distance function (in physical pixels, negative inside),
    // with a one-pixel anti-aliased edge. (x0, y0)-(x1, y1) is its bounding box.
    delegate float Sdf(float x, float y);
    static void Fill(uint* buf, int bw, int bh, float x0, float y0, float x1, float y1, Sdf sdf,
                     float alpha, float r, float g, float b)
    {
        int ix0 = Math.Max(0, (int)MathF.Floor(x0) - 1), ix1 = Math.Min(bw - 1, (int)MathF.Ceiling(x1) + 1);
        int iy0 = Math.Max(0, (int)MathF.Floor(y0) - 1), iy1 = Math.Min(bh - 1, (int)MathF.Ceiling(y1) + 1);
        for (int y = iy0; y <= iy1; y++)
        {
            uint* row = buf + (long)y * bw;
            for (int x = ix0; x <= ix1; x++)
            {
                float cov = Math.Clamp(0.5f - sdf(x + 0.5f, y + 0.5f), 0, 1);
                if (cov > 0) Blend(row + x, cov * alpha, r, g, b);
            }
        }
    }

    static void Circle(uint* buf, int bw, int bh, float cx, float cy, float rad, float r, float g, float b, float alpha = 1) =>
        Fill(buf, bw, bh, cx - rad, cy - rad, cx + rad, cy + rad,
             (x, y) => MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - rad, alpha, r, g, b);

    // A thick line with round ends
    static void Line(uint* buf, int bw, int bh, float ax, float ay, float bx, float by, float width,
                     float r, float g, float b, float alpha = 1)
    {
        float hw = width / 2, vx = bx - ax, vy = by - ay, len2 = MathF.Max(1e-4f, vx * vx + vy * vy);
        Fill(buf, bw, bh, MathF.Min(ax, bx) - hw, MathF.Min(ay, by) - hw, MathF.Max(ax, bx) + hw, MathF.Max(ay, by) + hw,
             (x, y) =>
             {
                 float tt = Math.Clamp(((x - ax) * vx + (y - ay) * vy) / len2, 0, 1);
                 float qx = x - ax - vx * tt, qy = y - ay - vy * tt;
                 return MathF.Sqrt(qx * qx + qy * qy) - hw;
             }, alpha, r, g, b);
    }

    // A rounded rectangle centered on (cx, cy), turned by angle (radians, clockwise on screen) around (ox, oy)
    static void RoundRect(uint* buf, int bw, int bh, float cx, float cy, float hw, float hh, float radius,
                          float angle, float ox, float oy, float r, float g, float b)
    {
        float cs = MathF.Cos(angle), sn = MathF.Sin(angle);
        // where the center ends up after turning around (ox, oy)
        float rcx = ox + (cx - ox) * cs - (cy - oy) * sn, rcy = oy + (cx - ox) * sn + (cy - oy) * cs;
        float ext = MathF.Sqrt(hw * hw + hh * hh);
        Fill(buf, bw, bh, rcx - ext, rcy - ext, rcx + ext, rcy + ext, (x, y) =>
        {
            // turn the pixel back into the rectangle's own frame
            float px = x - rcx, py = y - rcy;
            float lx = px * cs + py * sn, ly = -px * sn + py * cs;
            float qx = MathF.Abs(lx) - (hw - radius), qy = MathF.Abs(ly) - (hh - radius);
            float ox2 = MathF.Max(qx, 0), oy2 = MathF.Max(qy, 0);
            return MathF.Sqrt(ox2 * ox2 + oy2 * oy2) + MathF.Min(MathF.Max(qx, qy), 0) - radius;
        }, 1, r, g, b);
    }

    // A filled triangle (signed distance to its edges)
    static void Triangle(uint* buf, int bw, int bh, float ax, float ay, float bx, float by, float cx, float cy,
                         float r, float g, float b)
    {
        // make the winding consistent so "inside" is negative for every edge
        if ((bx - ax) * (cy - ay) - (by - ay) * (cx - ax) < 0) { (bx, cx) = (cx, bx); (by, cy) = (cy, by); }
        float Edge(float px, float py, float x0, float y0, float x1, float y1)
        {
            float ex = x1 - x0, ey = y1 - y0, l = MathF.Max(1e-4f, MathF.Sqrt(ex * ex + ey * ey));
            return ((px - x0) * ey - (py - y0) * ex) / l;   // > 0 outside this edge
        }
        float AX = ax, AY = ay, BX = bx, BY = by, CX = cx, CY = cy;
        Fill(buf, bw, bh, MathF.Min(ax, MathF.Min(bx, cx)), MathF.Min(ay, MathF.Min(by, cy)),
             MathF.Max(ax, MathF.Max(bx, cx)), MathF.Max(ay, MathF.Max(by, cy)),
             (x, y) => MathF.Max(Edge(x, y, AX, AY, BX, BY), MathF.Max(Edge(x, y, BX, BY, CX, CY), Edge(x, y, CX, CY, AX, AY))),
             1, r, g, b);
    }

    // A snowball: white with a soft blue-grey shade toward the lower right, a highlight top-left,
    // and little specks that turn as it rolls
    public static void Ball(uint* buf, int bw, int bh, Snowball ball, float s)
    {
        float cx = ball.X * s, cy = ball.Y * s, R = ball.R * s;
        float hx = cx - 0.12f * R * 2, hy = cy - 0.14f * R * 2;     // highlight, up and to the left
        int x0 = Math.Max(0, (int)MathF.Floor(cx - R) - 1), x1 = Math.Min(bw - 1, (int)MathF.Ceiling(cx + R) + 1);
        int y0 = Math.Max(0, (int)MathF.Floor(cy - R) - 1), y1 = Math.Min(bh - 1, (int)MathF.Ceiling(cy + R) + 1);
        for (int y = y0; y <= y1; y++)
        {
            uint* row = buf + (long)y * bw;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float d = MathF.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
                float cov = Math.Clamp(R - d + 0.5f, 0, 1);
                if (cov <= 0) continue;
                float u = MathF.Sqrt((px - hx) * (px - hx) + (py - hy) * (py - hy)) / (R * 1.1f);
                float rr, gg, bb;
                if (u < 0.55f) { float k = u / 0.55f; rr = 1 - 0.06f * k; gg = 1 - 0.04f * k; bb = 1; }
                else { float k = Math.Clamp((u - 0.55f) / 0.45f, 0, 1); rr = 0.94f - 0.26f * k; gg = 0.96f - 0.21f * k; bb = 1 - 0.12f * k; }
                Blend(row + x, cov, rr, gg, bb);
            }
        }
        foreach (var sp in ball.Specks)
        {
            float a = sp.Ang + ball.Rot;
            float sx = cx + MathF.Cos(a) * sp.Dist * R, sy = cy + MathF.Sin(a) * sp.Dist * R;
            Circle(buf, bw, bh, sx, sy, MathF.Max(0.6f, sp.Size * R), 0.66f, 0.73f, 0.87f, sp.Alpha);
        }
        if (ball.Decor == 1) Head(buf, bw, bh, cx, cy, R, s);
        else if (ball.Decor == 2) Body(buf, bw, bh, cx, cy, R, s);
    }

    // Snowman head: coal eyes, carrot nose, smile, red scarf
    static void Head(uint* buf, int bw, int bh, float cx, float cy, float R, float s)
    {
        foreach (float sx in new[] { -1f, 1f })
            Circle(buf, bw, bh, cx + sx * R * 0.3f, cy - R * 0.22f, MathF.Max(s, R * 0.09f), 0.08f, 0.08f, 0.08f);
        Triangle(buf, bw, bh, cx + R * 0.02f, cy - R * 0.06f, cx + R * 0.75f, cy + R * 0.04f, cx + R * 0.02f, cy + R * 0.12f,
                 0.96f, 0.52f, 0.13f);
        for (int k = 0; k < 5; k++)
        {
            float a = MathF.PI * (1.2f + 0.15f * k);
            Circle(buf, bw, bh, cx + MathF.Cos(a) * R * 0.42f, cy - MathF.Sin(a) * R * 0.42f + R * 0.05f,
                   MathF.Max(0.7f * s, R * 0.05f), 0.1f, 0.1f, 0.1f);
        }
        RoundRect(buf, bw, bh, cx, cy + R * 0.83f, R * 0.85f, R * 0.15f, R * 0.12f, 0, cx, cy, 0.82f, 0.16f, 0.18f);
        RoundRect(buf, bw, bh, cx + R * 0.44f, cy + R * 1.2f, R * 0.14f, R * 0.35f, R * 0.08f, 0.15f, cx, cy, 0.82f, 0.16f, 0.18f);
    }

    // Snowman body: three buttons and two stick arms
    static void Body(uint* buf, int bw, int bh, float cx, float cy, float B, float s)
    {
        for (int k = 0; k < 3; k++)
            Circle(buf, bw, bh, cx, cy - B * (0.35f - 0.33f * k), MathF.Max(0.8f * s, B * 0.07f), 0.08f, 0.08f, 0.08f);
        float lw = MathF.Max(1.2f * s, B * 0.09f);
        foreach (float sx in new[] { -1f, 1f })
        {
            Line(buf, bw, bh, cx + sx * B * 0.85f, cy - B * 0.15f, cx + sx * B * 1.75f, cy - B * 0.65f, lw, 0.42f, 0.28f, 0.16f);
            Line(buf, bw, bh, cx + sx * B * 1.4f, cy - B * 0.45f, cx + sx * B * 1.55f, cy - B * 0.85f, lw, 0.42f, 0.28f, 0.16f);
        }
    }

    // A string of fairy lights: a thin dark wire with colorful twinkling bulbs hanging from it
    public static void Lights(uint* buf, int bw, int bh, LightString ls, float t, float s)
    {
        for (int i = 1; i < ls.Wire.Count; i++)
        {
            var a = ls.Wire[i - 1]; var b = ls.Wire[i];
            if (a.hidden || b.hidden) continue;
            Line(buf, bw, bh, a.x * s, a.y * s, b.x * s, b.y * s, 1.2f * s, 0.12f, 0.12f, 0.12f, 0.85f);
        }
        for (int i = 0; i < ls.Bulbs.Count; i++)
        {
            var b = ls.Bulbs[i];
            if (b.Hidden) continue;
            float reveal = ls.Reveal(i, t);
            if (reveal <= 0) continue;
            float k = 0.75f + 0.25f * MathF.Sin(t * b.Speed + b.Phase);
            var (r, g, bl) = LightString.Colors[b.Color];
            Glow(buf, bw, bh, b.X * s, b.Y * s, Cfg.BulbGlow / 2 * s, 0.8f * k * reveal, r, g, bl);
            // keep the center colorful: only 20% toward white
            Glow(buf, bw, bh, b.X * s, b.Y * s, Cfg.BulbCore / 2 * s * 1.3f, (0.85f + 0.15f * k) * reveal,
                 r + (1 - r) * 0.2f, g + (1 - g) * 0.2f, bl + (1 - bl) * 0.2f);
        }
    }

    static readonly List<Snowball> drawOrder = new();

    public static void World(uint* buf, int bw, int bh, SnowWorld w, float s)
    {
        new Span<uint>(buf, bw * bh).Clear();
        // far + middle flakes, then piles, lights and snowballs, then near flakes and clumps on top
        foreach (var f in w.Flakes) if (!f.Clump && f.Layer < 2) Disc(buf, bw, bh, f.X * s, f.Y * s, f.Size * 1.1f * s, f.Alpha);
        foreach (var p in w.Piles.Values) Pile(buf, bw, bh, p, s);
        Pile(buf, bw, bh, w.Ground, s);
        foreach (var ls in w.Lights.Values) Lights(buf, bw, bh, ls, w.Time, s);
        // Lower balls first, so a snowman's head sits in front of its body; a dragged ball goes on top
        drawOrder.Clear();
        foreach (var b in w.Balls) if (!b.Hidden) drawOrder.Add(b);
        drawOrder.Sort((a, b) => a.Dragging != b.Dragging ? (a.Dragging ? 1 : -1) : b.Y.CompareTo(a.Y));
        foreach (var b in drawOrder) Ball(buf, bw, bh, b, s);
        foreach (var f in w.Flakes) if (f.Clump || f.Layer == 2) Disc(buf, bw, bh, f.X * s, f.Y * s, f.Size * 1.1f * s, f.Alpha);
    }

    // 32×32 tray icon: white six-armed snowflake on a blue circle (straight alpha, as icons expect)
    public static void TrayIcon(uint* buf, int n)
    {
        float c = n / 2f;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float px = x + 0.5f - c, py = y + 0.5f - c;
                float r = MathF.Sqrt(px * px + py * py);
                float bgA = Math.Clamp(c - 0.5f - r, 0, 1);
                float dist = 99;
                for (int k = 0; k < 6; k++)
                {
                    float ang = k * MathF.PI / 3, ex = MathF.Cos(ang), ey = MathF.Sin(ang);
                    float tt = Math.Clamp(px * ex + py * ey, 0, c * 0.72f);
                    float qx = px - ex * tt, qy = py - ey * tt;
                    dist = MathF.Min(dist, MathF.Sqrt(qx * qx + qy * qy));
                }
                float flake = Math.Clamp(1.7f - dist, 0, 1) * bgA;
                float rr = 0.25f + 0.75f * flake, gg = 0.47f + 0.53f * flake, bb = 0.85f + 0.15f * flake;
                uint a = (uint)(bgA * 255);
                buf[y * n + x] = (a << 24) | ((uint)(rr * 255) << 16) | ((uint)(gg * 255) << 8) | (uint)(bb * 255);
            }
    }
}

// MARK: - Win32 plumbing

static unsafe class Program
{
    // --- constants
    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8,
               WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int GWL_EXSTYLE = -20, GWL_STYLE = -16;
    const uint WS_CAPTION = 0x00C00000, GW_OWNER = 4;
    const uint WM_DESTROY = 0x2, WM_TIMER = 0x113, WM_DISPLAYCHANGE = 0x7E, WM_NULL = 0,
               WM_APP = 0x8000, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205,
               WM_MOUSEACTIVATE = 0x21, WM_SETCURSOR = 0x20, WM_CAPTURECHANGED = 0x215;
    const int MA_NOACTIVATE = 3, VK_LBUTTON = 1, IDC_ARROW = 32512, IDC_HAND = 32649;
    const uint MB_OK = 0, MB_ICONINFORMATION = 0x40, MB_SETFOREGROUND = 0x10000, MB_TOPMOST = 0x40000;
    const int TIMER_FRAME = 1, TIMER_GUIDE = 2;
    const uint WM_TRAY = WM_APP + 1;
    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    const uint ULW_ALPHA = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    const uint MF_STRING = 0, MF_CHECKED = 8, MF_POPUP = 0x10, MF_SEPARATOR = 0x800;
    const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;
    const int CMD_PAUSE = 1, CMD_CLEAR = 2, CMD_QUIT = 3, CMD_LIGHTS = 4, CMD_GUIDE = 5,
              CMD_LIGHT = 10, CMD_MEDIUM = 11, CMD_HEAVY = 12;

    // --- structs
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATAW
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public int fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    delegate IntPtr WndProcFn(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // --- imports
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
                                         IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                           ref POINT pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hWnd, UIntPtr id);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, IntPtr bits);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    // --- state
    static WndProcFn wndProc;                 // kept alive so the GC doesn't collect the callback
    static EnumWindowsProc enumProc;
    static IntPtr overlay, tray, screenDC, memDC, dib, bits, trayIcon;
    static int bw, bh;                        // buffer size in physical pixels
    static float scale = 1;                   // physical pixels per logical pixel (DPI / 96)
    static SnowWorld world;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static double lastT;
    static bool paused;
    static bool catching;                     // overlay currently takes mouse clicks (mouse over a snowball)
    static bool dragging;
    static IntPtr handCursor, arrowCursor;
    static float mouseX, mouseY;              // logical pixels
    static readonly Dictionary<string, string> settings = new();
    static readonly string settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinterIsComing", "settings.txt");
    static readonly List<Win> found = new();
    static readonly StringBuilder clsName = new(64);

    [STAThread]
    static void Main()
    {
        SetProcessDpiAwarenessContext((IntPtr)(-4));   // per-monitor DPI aware v2: real pixel coordinates
        IntPtr inst = GetModuleHandleW(null);

        wndProc = WndProc;
        enumProc = EnumProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = inst,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
            lpszClassName = "WinterIsComingWnd",
        };
        RegisterClassExW(ref wc);

        bw = GetSystemMetrics(0);   // SM_CXSCREEN (primary monitor)
        bh = GetSystemMetrics(1);   // SM_CYSCREEN
        // Transparent, click-through, always on top, never takes focus, not in Alt+Tab
        overlay = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                                  "WinterIsComingWnd", "WinterIsComing", WS_POPUP, 0, 0, bw, bh,
                                  IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        // A hidden window that owns the tray icon and its menu
        tray = CreateWindowExW(WS_EX_TOOLWINDOW, "WinterIsComingWnd", "WinterIsComing tray", WS_POPUP, 0, 0, 0, 0,
                               IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);

        uint dpi = GetDpiForWindow(overlay);
        scale = dpi > 0 ? dpi / 96f : 1;
        screenDC = GetDC(IntPtr.Zero);
        handCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_HAND);
        arrowCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW);
        CreateSurface();
        LoadSettings();
        world = new SnowWorld(bw / scale, bh / scale) { LightsOn = Setting("fairyLights") };   // lights are off by default

        ShowWindow(overlay, SW_SHOWNOACTIVATE);
        AddTrayIcon();
        SetTimer(overlay, (UIntPtr)TIMER_FRAME, Cfg.FrameMs, IntPtr.Zero);
        // Show the how-to-play tips once, the very first time the app opens
        if (!Setting("seenGuide"))
        {
            SaveSetting("seenGuide", true);
            SetTimer(overlay, (UIntPtr)TIMER_GUIDE, 1000, IntPtr.Zero);
        }

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    static void CreateSurface()
    {
        if (memDC == IntPtr.Zero) memDC = CreateCompatibleDC(IntPtr.Zero);
        if (dib != IntPtr.Zero) DeleteObject(dib);
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = bw, biHeight = -bh,   // negative = top-down rows
                biPlanes = 1, biBitCount = 32, biCompression = 0,
            }
        };
        dib = CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(memDC, dib);
    }

    static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                if (hWnd != overlay) return IntPtr.Zero;
                if ((long)wParam == TIMER_GUIDE) { KillTimer(overlay, (UIntPtr)TIMER_GUIDE); ShowGuide(); }
                else Tick();
                return IntPtr.Zero;
            case WM_MOUSEACTIVATE:
                return (IntPtr)MA_NOACTIVATE;           // grabbing a snowball never steals focus from your apps
            case WM_SETCURSOR:
                if (hWnd == overlay) { SetCursor(handCursor); return (IntPtr)1; }
                break;
            case WM_LBUTTONDOWN:
                if (hWnd == overlay)
                {
                    UpdateMouse();
                    if (world.MouseDown(mouseX, mouseY)) { dragging = true; SetCapture(overlay); }
                }
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                if (hWnd == overlay) EndDrag();
                else break;
                return IntPtr.Zero;
            case WM_CAPTURECHANGED:
                if (hWnd == overlay && dragging) EndDrag();
                return IntPtr.Zero;
            case WM_DISPLAYCHANGE:
                if (hWnd == overlay)
                {
                    bw = GetSystemMetrics(0);
                    bh = GetSystemMetrics(1);
                    CreateSurface();
                    world.Resize(bw / scale, bh / scale);
                }
                return IntPtr.Zero;
            case WM_TRAY:
                uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
                if (ev == WM_RBUTTONUP || ev == WM_LBUTTONUP) ShowMenu();
                return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    static void Tick()
    {
        double now = clock.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(now - lastT, 1.0 / 20);
        lastT = now;
        if (paused) return;

        UpdateMouse();
        // Safety net: if the button was released somewhere we didn't hear about, let go of the ball
        if (dragging && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) EndDrag();
        world.Update(dt, ReadWindows, ReadTaskbarTop, mouseX, mouseY);
        SetCatching(world.WantsMouse(mouseX, mouseY));
        Render.World((uint*)bits, bw, bh, world, scale);

        var dst = new POINT();
        var src = new POINT();
        var size = new SIZE { cx = bw, cy = bh };
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };   // AC_SRC_ALPHA
        UpdateLayeredWindow(overlay, screenDC, ref dst, ref size, memDC, ref src, 0, ref blend, ULW_ALPHA);
    }

    // MARK: Mouse

    static void UpdateMouse()
    {
        GetCursorPos(out POINT pt);
        mouseX = pt.X / scale;
        mouseY = pt.Y / scale;
    }

    static void EndDrag()
    {
        if (!dragging) return;
        dragging = false;
        world.MouseUp();
        ReleaseCapture();
    }

    /// The overlay is click-through (WS_EX_TRANSPARENT) except while the mouse is over a snowball,
    /// so you can grab the ball without clicking whatever is underneath.
    static void SetCatching(bool on)
    {
        if (on == catching) return;
        catching = on;
        long ex = GetWindowLongPtrW(overlay, GWL_EXSTYLE).ToInt64();
        ex = on ? ex & ~(long)WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        SetWindowLongPtrW(overlay, GWL_EXSTYLE, (IntPtr)ex);
        if (!on) SetCursor(arrowCursor);
    }

    // MARK: Settings (a tiny key=value file in %APPDATA%\WinterIsComing)

    static void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            foreach (var line in File.ReadAllLines(settingsPath))
            {
                int i = line.IndexOf('=');
                if (i > 0) settings[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
        }
        catch { /* no settings yet, or unreadable: use the defaults */ }
    }

    static bool Setting(string key) => settings.TryGetValue(key, out var v) && v == "1";

    static void SaveSetting(string key, bool value)
    {
        settings[key] = value ? "1" : "0";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            var lines = new List<string>();
            foreach (var kv in settings) lines.Add(kv.Key + "=" + kv.Value);
            File.WriteAllLines(settingsPath, lines);
        }
        catch { /* not being able to remember a setting is not worth crashing over */ }
    }

    /// A small how-to-play note: the app has no window, so this is where people learn the tricks
    static void ShowGuide()
    {
        const string text =
            "It's snowing on your desktop! Here's what you can do:\n\n" +
            "•  Roll a snowball: slide your mouse sideways over snow\n" +
            "•  Push it: roll one snowball into another\n" +
            "•  Carry it: hold the left mouse button on a snowball and drag\n" +
            "•  Build a snowman: stack three snowballs\n" +
            "•  Make snow fly: drag a window\n" +
            "•  Hang fairy lights: tray icon → Fairy lights\n\n" +
            "Find everything under the ❄ icon in the system tray (bottom right; click ^ if you don't see it).";
        MessageBoxW(IntPtr.Zero, text, "Welcome to WinterIsComing ❄", MB_OK | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST);
    }

    // MARK: Windows on screen

    static List<Win> ReadWindows()
    {
        found.Clear();
        EnumWindows(enumProc, IntPtr.Zero);       // top-level windows, front to back
        return new List<Win>(found);
    }

    static bool EnumProc(IntPtr h, IntPtr _)
    {
        if (h == overlay || h == tray) return true;
        if (!IsWindowVisible(h) || IsIconic(h)) return true;
        long ex = GetWindowLongPtrW(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return true;                       // tool windows, popups
        if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return true;  // other overlays
        // Menus, right-click menus and other popups: no title bar, and owned by an app window, always on top
        // or never activated. Real app windows (even frameless ones) are none of these.
        long style = GetWindowLongPtrW(h, GWL_STYLE).ToInt64();
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
        if (!hasCaption && (owned || (ex & WS_EX_TOPMOST) != 0 || (ex & WS_EX_NOACTIVATE) != 0)) return true;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true; // other desktops, hidden UWP
        if (GetWindowTextLengthW(h) == 0) return true;
        clsName.Clear();
        GetClassNameW(h, clsName, clsName.Capacity);
        string cls = clsName.ToString();
        if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;

        // The visible frame (GetWindowRect includes invisible resize borders on Windows 10/11)
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT rc, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(h, out rc);
        float l = rc.Left / scale, t = rc.Top / scale, r = rc.Right / scale, b = rc.Bottom / scale;
        if (r - l < 80 || b - t < 40) return true;
        found.Add(new Win(h.ToInt64(), l, t, r, b));
        return true;
    }

    // Top edge of the taskbar when it sits at the bottom and is visible; otherwise null
    static float? ReadTaskbarTop()
    {
        IntPtr tb = FindWindowW("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero || !IsWindowVisible(tb) || !GetWindowRect(tb, out RECT rc)) return null;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= h || rc.Top < bh / 2 || rc.Top >= bh - 4) return null;   // not at the bottom, or auto-hidden
        return rc.Top / scale;
    }

    // MARK: Tray icon and menu

    static void AddTrayIcon()
    {
        const int n = 32;
        var bmi = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = n, biHeight = -n, biPlanes = 1, biBitCount = 32,
            }
        };
        IntPtr color = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr px, IntPtr.Zero, 0);
        Render.TrayIcon((uint*)px, n);
        IntPtr mask = CreateBitmap(n, n, 1, 1, IntPtr.Zero);
        var info = new ICONINFO { fIcon = 1, hbmMask = mask, hbmColor = color };
        trayIcon = CreateIconIndirect(ref info);
        DeleteObject(color);
        DeleteObject(mask);

        var nid = TrayData();
        nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    static NOTIFYICONDATAW TrayData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = tray, uID = 1, uCallbackMessage = WM_TRAY, hIcon = trayIcon,
        szTip = "WinterIsComing ❄", szInfo = "", szInfoTitle = "",
    };

    static void ShowMenu()
    {
        IntPtr menu = CreatePopupMenu(), amount = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_PAUSE, paused ? "Resume snow" : "Pause snow");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 120 ? MF_CHECKED : 0), (UIntPtr)CMD_LIGHT, "Light");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 260 ? MF_CHECKED : 0), (UIntPtr)CMD_MEDIUM, "Medium");
        AppendMenuW(amount, MF_STRING | (Cfg.TargetFlakes == 500 ? MF_CHECKED : 0), (UIntPtr)CMD_HEAVY, "Heavy");
        AppendMenuW(menu, MF_POPUP, (UIntPtr)(ulong)amount.ToInt64(), "Snow amount");
        AppendMenuW(menu, MF_STRING | (world.LightsOn ? MF_CHECKED : 0), (UIntPtr)CMD_LIGHTS, "Fairy lights ✨");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_CLEAR, "Clear snow");
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_GUIDE, "How to play");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, MF_STRING, (UIntPtr)CMD_QUIT, "Quit");

        GetCursorPos(out POINT pt);
        SetForegroundWindow(tray);   // so the menu closes when you click elsewhere
        int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, tray, IntPtr.Zero);
        PostMessageW(tray, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);   // also destroys the submenu

        switch (cmd)
        {
            case CMD_PAUSE:
                paused = !paused;
                if (paused) { EndDrag(); SetCatching(false); }
                ShowWindow(overlay, paused ? SW_HIDE : SW_SHOWNOACTIVATE);
                break;
            case CMD_LIGHTS:
                world.LightsOn = !world.LightsOn;
                SaveSetting("fairyLights", world.LightsOn);
                break;
            case CMD_GUIDE: ShowGuide(); break;
            case CMD_LIGHT: Cfg.TargetFlakes = 120; break;
            case CMD_MEDIUM: Cfg.TargetFlakes = 260; break;
            case CMD_HEAVY: Cfg.TargetFlakes = 500; break;
            case CMD_CLEAR: EndDrag(); world.ClearAll(); break;
            case CMD_QUIT: Quit(); break;
        }
    }

    static void Quit()
    {
        KillTimer(overlay, (UIntPtr)TIMER_FRAME);
        var nid = TrayData();
        Shell_NotifyIconW(NIM_DELETE, ref nid);
        DestroyWindow(overlay);
        DestroyWindow(tray);
        PostQuitMessage(0);
    }
}
