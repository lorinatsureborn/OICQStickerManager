using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace OICQStickerManager.Services;

/// <summary>
/// 表情按钮像素模板——a11y 树休眠回退的弹药库。
/// 背景（2026-10-05 冷启动实证）：QQ 9.9.21 重启后渲染器无障碍树外部无法激活——WM_GETOBJECT
/// （顶层 OBJID_CLIENT / D3D 子窗口 / UiaRootObjectId 0x25）、UIA 客户端连接与事件订阅、SPI 屏幕
/// 阅读器标志、讲述人实际读取焦点窗口，全部无效，树恒为 2 节点壳层。焦点命中永不出现 →
/// 按钮矩形缓存无法建立 → 钩子三重校验第一关即拒，共存触发链整条死掉且零报错。
/// 模板 = 树还活着的会话里把按钮图标像素存盘（含 DPI 缩放元数据，按缩放各存一份）；
/// 休眠期由鼠标钩子对 QQ 窗口内的每次按下在点击点邻域做模板匹配（SAD，±18px，毫秒级），
/// 命中即重建按钮几何缓存并复用钩子原有乐观打开链路。点击点锚定搜索 → 无需全窗口扫描。
/// 模板缺失/主题改版/缩放不符时静默退出，行为与无回退时完全一致，树恢复后自动自愈。
/// </summary>
internal static class EmojiButtonTemplate
{
    private const int SearchRadius = 18;      // 匹配中心允许偏离点击点的半径（物理像素）
    // 命中判定：二值形状异或距离（0-1，按字形像素数归一：完全错配=1，精确对齐≈0）。
    // RGB 直比不可行——hover 态图标会整体换色（灰→蓝，实测），但字形形状不变；模板/候选
    // 各自相对"背景中位色"二值化后比对，对 hover/主题/换色全部免疫。
    // 阈值 0.35：±1px 边缘反锯齿翻转 ~0.1-0.25，相邻异形图标/平坦区 ≥0.8（实测校准）。
    private const double XorThreshold = 0.35;
    private const int BgDistGlyph = 150;      // 像素与背景中位色的 BGR 总差超过此值 = 字形（深灰字形 ~200+，蓝色 ~150+）
    private const string FileName = "emoji-btn-template.json";

    private sealed class Scaled // 按 DPI 预缩放后的匹配弹药
    {
        public byte[] Pixels = Array.Empty<byte>(); // BGRA32
        public int Size;
        public byte[] BgMedian = new byte[3];       // 背景中位色（B,G,R）：模板主体是工具栏背景，中位数即它
        public int MaskCount;                       // 字形像素数（Shapes 距离的分母之一）
    }

    private sealed class Data
    {
        public int Version { get; set; } = 2;
        public double Scale { get; set; } = 1.0;  // 采集时按钮所在窗口的 DPI 缩放（96 = 100%）
        public int Size { get; set; }             // 模板边长（物理像素，正方形）
        public string? QQVersion { get; set; }
        public string? CapturedAt { get; set; }
        public string? PngBase64 { get; set; }
    }

    /// <summary>诊断日志汇（watcher 构造时接入 asuka-watcher.log，保持单一日志面）。</summary>
    public static Action<string>? LogSink;

    private static readonly object Gate = new();
    private static int _loadAttempted;          // 磁盘只探一次（含失败），此后 Available 走纯内存
    private static volatile bool _available;
    private static Data? _disk;
    private static readonly Dictionary<int, Scaled> _scaled = new(); // key = 目标 DPI 的千分比（防浮点键）
    private static int _capturing;

    private static string StorePath() =>
        Path.Combine(AppDataDirectory.Root, FileName);

    /// <summary>磁盘上是否存在可用模板（懒加载一次；钩子线程每击调用，必须零 IO）。</summary>
    public static bool Available
    {
        get
        {
            if (Volatile.Read(ref _loadAttempted) == 0 && Interlocked.Exchange(ref _loadAttempted, 1) == 0)
                Load();
            return _available;
        }
    }

    private static void Load()
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path)) return;
            _disk = JsonSerializer.Deserialize<Data>(File.ReadAllText(path));
            if (_disk?.PngBase64 == null || _disk.Size <= 0) { _disk = null; return; }
            _available = true;
            LogSink?.Invoke($"pixel template loaded (scale={_disk.Scale}, size={_disk.Size}, captured={_disk.CapturedAt})");
        }
        catch (Exception ex) { LogSink?.Invoke("pixel template load failed: " + ex.Message); }
    }

    /// <summary>树激活路径的顺带采集：按钮矩形缓存建立时调用（后台线程）。同一 DPI 缩放只存一份，
    /// 已存在即跳过；中心点被其他窗口覆盖时放弃（截到别家像素的模板比没有更糟）。</summary>
    public static void CaptureFromScreen(IntPtr hwnd, Rect absButtonRect, string qqVersion, bool force = false)
    {
        if (Interlocked.Exchange(ref _capturing, 1) == 1) return;
        try
        {
            var _ = Available; // 确保磁盘状态已知（决定是否需要采集）
            var scale = GetWindowScale(hwnd);
            if (!force && _disk?.Version >= 2 && Math.Abs(_disk.Scale - scale) < 0.01) return;
            int size = Math.Clamp((int)Math.Ceiling(24 * scale) + 16, 40, 72);
            int cx = (int)(absButtonRect.Left + absButtonRect.Width / 2);
            int cy = (int)(absButtonRect.Top + absButtonRect.Height / 2);
            var region = new Rect(cx - size / 2, cy - size / 2, size, size);
            // A hover/click cursor can be composited into the GDI capture. Wait for
            // the user to leave; never move their pointer just to collect a template.
            long until = Environment.TickCount64 + 5000;
            while (GetCursorPos(out var cursor) && !CursorClearOfTemplate(region, new(cursor.X, cursor.Y), scale))
            {
                if (Environment.TickCount64 >= until) return;
                Thread.Sleep(100);
            }
            // 中心点必须仍在宿主窗口上（按钮可见）：被快捷面板/其他窗口盖住就放弃本次采集
            var hit = WindowFromPoint(new POINT { X = cx, Y = cy });
            if (hit != IntPtr.Zero && GetAncestor(hit, GA_ROOT) != hwnd) return;
            var px = CaptureScreenPixels(cx, cy, size, out var stride);
            if (px == null) return;
            if (GetCursorPos(out var after) && !CursorClearOfTemplate(region, new(after.X, after.Y), scale)) return;
            var data = new Data
            {
                Scale = scale,
                Size = size,
                QQVersion = qqVersion,
                CapturedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                PngBase64 = ToPngBase64(px, size, size, stride),
            };
            var path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, path, overwrite: true);
            lock (Gate)
            {
                _disk = data;
                _scaled.Clear(); // 旧缩放弹药作废
                _available = true;
            }
            LogSink?.Invoke($"pixel template captured (scale={scale}, size={size}, from focus-hit rect)");
        }
        catch (Exception ex) { LogSink?.Invoke("pixel template capture failed: " + ex.Message); }
        finally { Volatile.Write(ref _capturing, 0); }
    }

    internal static bool CursorClearOfTemplate(Rect region, Point cursor, double scale)
    {
        if (region.IsEmpty || !double.IsFinite(scale) || scale <= 0) return false;
        region.Inflate(64 * scale, 64 * scale);
        return !region.Contains(cursor);
    }

    /// <summary>休眠回退匹配：点击点邻域找按钮模板。命中返回模板尺寸的绝对矩形（物理像素）。
    /// 零 UIA；一次 GDI 屏幕读取（模板+2×半径 见方）+ 带安全剪枝的 SAD 搜索，毫秒级。</summary>
    public static bool TryMatch(int clickX, int clickY, IntPtr rootHwnd, out Rect matched)
    {
        matched = Rect.Empty;
        if (!Available) return false;
        Data? disk;
        lock (Gate) { disk = _disk; }
        if (disk?.PngBase64 == null) return false;
        var scale = GetWindowScale(rootHwnd);
        var tpl = GetScaled(disk, scale);
        if (tpl == null) return false;

        int s = tpl.Size;
        int region = s + 2 * SearchRadius + 2;
        int rx = clickX - region / 2, ry = clickY - region / 2;
        // 取屏按"中心点"语义（内部自行减半边）：必须传点击点，传 rx/ry 会把截屏区域
        // 整体偏移半个 region，搜索落在按钮上方 39px 的消息区（实测 pixel match 必失败）
        var buf = CaptureScreenPixels(clickX, clickY, region, out var stride);
        if (buf == null) return false;

        SearchBest(tpl, buf, region, stride, out var best, out var bx, out var by);
        if (bx < 0 || best > XorThreshold) return false;
        matched = new Rect(rx + bx, ry + by, s, s);
        return true;
    }

    /// <summary>二值形状全搜（±SearchRadius）：模板与候选各自相对背景中位色二值化，输出最小异或距离
    /// 与模板在采样区域内的左上坐标（非负）。bestX&lt;0 仅表示未找到。
    /// 剪枝：距离只增不减，已超
    /// 当前最优的部分候选可放弃。</summary>
    private static void SearchBest(Scaled tpl, byte[] region, int regionSize, int stride, out double bestScore, out int bestX, out int bestY)
    {
        int s = tpl.Size;
        var bg = tpl.BgMedian;
        // 模板二值化：字形=1（构建掩码时已定），逐候选只需对 region 二值化后按掩码累计异或
        int bestMismatch = int.MaxValue;
        bestScore = double.MaxValue; bestX = -1; bestY = -1;
        bool found = false;
        int c0 = regionSize / 2 - s / 2;
        for (int dy = -SearchRadius; dy <= SearchRadius; dy++)
        {
            int ry = c0 + dy;
            if (ry < 0 || ry + s > regionSize) continue;
            for (int dx = -SearchRadius; dx <= SearchRadius; dx++)
            {
                int rxc = c0 + dx;
                if (rxc < 0 || rxc + s > regionSize) continue;
                int mismatch = 0;
                // 剪枝：异或数只增不减，超过当前最优即放弃（无字形区可整段跳过由内圈 break 承担）
                int cap = found ? bestMismatch : int.MaxValue;
                var complete = true;
                for (int y = 0; y < s && complete; y++)
                {
                    int rRow = (ry + y) * stride + rxc * 4;
                    int tRow = y * s * 4;
                    for (int x = 0; x < s; x++)
                    {
                        int ri = rRow + x * 4;
                        int dist = Math.Abs(region[ri] - bg[0]) + Math.Abs(region[ri + 1] - bg[1]) + Math.Abs(region[ri + 2] - bg[2]);
                        bool regionGlyph = dist > BgDistGlyph;
                        bool tplGlyph = Math.Abs(tpl.Pixels[tRow + x * 4] - bg[0]) + Math.Abs(tpl.Pixels[tRow + x * 4 + 1] - bg[1]) + Math.Abs(tpl.Pixels[tRow + x * 4 + 2] - bg[2]) > BgDistGlyph;
                        if (regionGlyph != tplGlyph && ++mismatch >= cap) { complete = false; break; }
                    }
                }
                if (complete && mismatch < bestMismatch)
                {
                    bestMismatch = mismatch;
                    bestX = rxc; bestY = ry;
                    found = true;
                }
            }
        }
        if (found) bestScore = bestMismatch / (double)Math.Max(1, tpl.MaskCount);
    }

    // --- 离线自测（--pixel-selftest）：同一匹配核心跑截图文件，阈值校准与回归用 ---

    /// <summary>在截图文件上跑匹配核心。clicks 为 "x,y" 分号串（物理像素）；输出每点最优均差。</summary>
    public static string SelfTest(string imagePath, string clicks)
    {
        var frame = BitmapFrame.Create(new Uri(imagePath));
        int w = frame.PixelWidth, h = frame.PixelHeight;
        var img = new byte[w * h * 4];
        frame.CopyPixels(img, w * 4, 0);
        var _ = Available; // 确保磁盘模板已加载
        Data? disk;
        lock (Gate) { disk = _disk; }
        if (disk?.PngBase64 == null) return "NO TEMPLATE (load " + StorePath() + " first)";
        // 目标缩放取模板自身：自测的是匹配核心（1:1），不是跨 DPI 缩放
        var tpl = GetScaled(disk, disk.Scale);
        if (tpl == null) return "TEMPLATE DECODE FAILED";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"template size={tpl.Size} scale={disk.Scale}, image {w}x{h}, xorThreshold={XorThreshold}");
        foreach (var part in clicks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var xy = part.Split(',');
            if (xy.Length != 2 || !int.TryParse(xy[0], out var cx) || !int.TryParse(xy[1], out var cy)) continue;
            int s = tpl.Size;
            int region = s + 2 * SearchRadius + 2;
            var buf = new byte[region * region * 4];
            for (int y = 0; y < region; y++)
            {
                int sy = cy - region / 2 + y;
                if (sy < 0 || sy >= h) continue;
                int sx = cx - region / 2;
                int srcX = Math.Max(0, sx);
                int dstX = srcX - sx;
                int copyW = Math.Min(region - dstX, w - srcX);
                if (copyW <= 0) continue;
                Buffer.BlockCopy(img, (sy * w + srcX) * 4, buf, (y * region + dstX) * 4, copyW * 4);
            }
            SearchBest(tpl, buf, region, region * 4, out var score, out var bx, out var by);
            int centerOffset = region / 2 - s / 2;
            sb.AppendLine($"click=({cx},{cy}) xor={score:F3} offset=({bx - centerOffset},{by - centerOffset}) => {(score <= XorThreshold ? "HIT" : "miss")}");
        }
        return sb.ToString();
    }

    /// <summary>从文件加载模板（自测引导：绕过屏幕采集，直接把截图裁块存成模板）。</summary>
    public static string LoadFromFile(string imagePath, int centerX, int centerY, double scale)
    {
        try
        {
            var frame = BitmapFrame.Create(new Uri(imagePath));
            int size = Math.Clamp((int)Math.Ceiling(24 * scale) + 16, 40, 72);
            var cropped = new CroppedBitmap(frame, new Int32Rect(centerX - size / 2, centerY - size / 2, size, size));
            var px = new byte[size * size * 4];
            cropped.CopyPixels(px, size * 4, 0);
            var data = new Data
            {
                Scale = scale,
                Size = size,
                QQVersion = "bootstrap",
                CapturedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                PngBase64 = ToPngBase64(px, size, size, size * 4),
            };
            var path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, path, overwrite: true);
            lock (Gate) { _disk = data; _scaled.Clear(); _available = true; Volatile.Write(ref _loadAttempted, 1); }
            return $"template saved ({size}x{size} @scale {scale}) -> {path}";
        }
        catch (Exception ex) { return "LOAD FAILED: " + ex.Message; }
    }

    // --- 内部：缩放适配 / 屏幕读取 / PNG 编解码 / DPI ---

    private static Scaled? GetScaled(Data disk, double targetScale)
    {
        int key = (int)Math.Round(targetScale * 1000);
        lock (Gate)
        {
            if (_scaled.TryGetValue(key, out var hit)) return hit;
        }
        try
        {
            var png = Convert.FromBase64String(disk.PngBase64!);
            var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Scaled scaled;
            if (Math.Abs(disk.Scale - targetScale) < 0.01)
            {
                var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
                frame.CopyPixels(px, frame.PixelWidth * 4, 0);
                scaled = new Scaled { Pixels = px, Size = frame.PixelWidth };
            }
            else
            {
                // 目标窗口 DPI 与采集时不同：预缩放一次并缓存（跨屏拖动/混合 DPI 环境）
                var tb = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(targetScale / disk.Scale, targetScale / disk.Scale));
                tb.Freeze();
                int size = tb.PixelWidth;
                var px = new byte[size * size * 4];
                tb.CopyPixels(px, size * 4, 0);
                scaled = new Scaled { Pixels = px, Size = size };
            }
            // 背景中位色：模板主体是工具栏背景（字形只占 ~12%），每通道中位数即背景。
            // 二值形状匹配的参照色——对 hover 高亮/主题换色天然免疫。
            scaled.BgMedian = MedianColor(scaled.Pixels);
            scaled.MaskCount = 0;
            for (int i = 0; i < scaled.Pixels.Length; i += 4)
            {
                int dist = Math.Abs(scaled.Pixels[i] - scaled.BgMedian[0]) + Math.Abs(scaled.Pixels[i + 1] - scaled.BgMedian[1]) + Math.Abs(scaled.Pixels[i + 2] - scaled.BgMedian[2]);
                if (dist > BgDistGlyph) scaled.MaskCount++;
            }
            lock (Gate) _scaled[key] = scaled;
            return scaled;
        }
        catch (Exception ex) { LogSink?.Invoke("pixel template decode failed: " + ex.Message); return null; }
    }

    private static byte[] MedianColor(byte[] bgra)
    {
        var hist = new int[3, 256];
        int n = bgra.Length / 4;
        for (int i = 0; i < n; i++)
        {
            hist[0, bgra[i * 4]]++; hist[1, bgra[i * 4 + 1]]++; hist[2, bgra[i * 4 + 2]]++;
        }
        var med = new byte[3];
        for (int c = 0; c < 3; c++)
        {
            int acc = 0;
            for (int v = 0; v < 256; v++)
            {
                acc += hist[c, v];
                if (acc >= (n + 1) / 2) { med[c] = (byte)v; break; }
            }
        }
        return med;
    }

    /// <summary>GDI 屏幕读取：BGRA32 顶层像素。部分在屏外/最低层会话（断开的 RDP）会拿到黑块，匹配自然不命中。</summary>
    private static byte[]? CaptureScreenPixels(int x, int y, int size, out int stride)
    {
        stride = size * 4;
        var bmi = new BITMAPINFO { bmiHeader = new BITMAPINFOHEADER { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32, biCompression = 0 } };
        IntPtr hdcScreen = GetDC(IntPtr.Zero);
        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr pBits = IntPtr.Zero;
        IntPtr hbmp = CreateDIBSection(hdcMem, ref bmi, 0 /*DIB_RGB_COLORS*/, out pBits, IntPtr.Zero, 0);
        byte[]? result = null;
        if (hbmp != IntPtr.Zero && pBits != IntPtr.Zero)
        {
            var old = SelectObject(hdcMem, hbmp);
            if (BitBlt(hdcMem, 0, 0, size, size, hdcScreen, x - size / 2, y - size / 2, SRCCOPY | CAPTUREBLT))
            {
                result = new byte[size * size * 4];
                Marshal.Copy(pBits, result, 0, result.Length);
            }
            SelectObject(hdcMem, old);
            DeleteObject(hbmp);
        }
        DeleteDC(hdcMem);
        ReleaseDC(IntPtr.Zero, hdcScreen);
        return result;
    }

    private static string ToPngBase64(byte[] bgra, int w, int h, int stride)
    {
        var source = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static double GetWindowScale(IntPtr hwnd)
    {
        try { return GetDpiForWindow(hwnd) / 96.0; }
        catch { return GetSystemScale(); }
    }

    private static double GetSystemScale()
    {
        var hdc = GetDC(IntPtr.Zero);
        try { return GetDeviceCaps(hdc, 88 /*LOGPIXELSX*/) / 96.0; }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public int bmiColors; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern int GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO bmi, uint usage, out IntPtr pBits, IntPtr hSection, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int dx, int dy, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);

    private const uint GA_ROOT = 2;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
}
