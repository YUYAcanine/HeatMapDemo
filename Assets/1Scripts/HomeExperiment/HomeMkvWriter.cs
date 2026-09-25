using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Azure.Kinect.Sensor;

// Azure Kinect 公式の録画形式(MKV)でキャプチャを書き出す。Assets/Plugins の k4arecord.dll を直接呼ぶ。
// k4arecorder.exe と同じファイルになるので、あとから pyk4a / Azure Kinect SDK / ffmpeg などで読める。
// (カラー・深度・赤外線の各トラックと、キネクトの校正情報・シリアル番号が MKV に入る)
//
// C# のラッパー(Microsoft.Azure.Kinect.Sensor)は録画の機能を持たず、ネイティブのハンドルも公開していないので、
// Device / Capture の内部のハンドルをリフレクションで取り出して渡す。
public sealed class HomeMkvWriter : IDisposable
{
    private const string RecordDll = "k4arecord";
    private const int Succeeded = 0;

    private IntPtr handle;

    public string Path { get; }
    public int WrittenCount { get; private set; }

    public HomeMkvWriter(string path, Device device, DeviceConfiguration configuration, params (string name, string value)[] tags)
    {
        Path = path;

        NativeConfiguration native = new NativeConfiguration
        {
            colorFormat = (int)configuration.ColorFormat,
            colorResolution = (int)configuration.ColorResolution,
            depthMode = (int)configuration.DepthMode,
            cameraFps = (int)configuration.CameraFPS,
            synchronizedImagesOnly = configuration.SynchronizedImagesOnly,
            depthDelayOffColorUsec = (int)(configuration.DepthDelayOffColor.Ticks / 10),
            wiredSyncMode = (int)configuration.WiredSyncMode,
            subordinateDelayOffMasterUsec = (uint)(configuration.SuboridinateDelayOffMaster.Ticks / 10),
            disableStreamingIndicator = configuration.DisableStreamingIndicator
        };

        Check(k4a_record_create(path, GetNativeHandle(device), native, out handle), "k4a_record_create");

        try
        {
            foreach ((string name, string value) in tags)
                Check(k4a_record_add_tag(handle, name, value ?? ""), "k4a_record_add_tag");

            Check(k4a_record_write_header(handle), "k4a_record_write_header");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // CopyCapture で作ったコピーを書く(コピーの解放は呼び出し側で ReleaseCapture する)
    public void Write(IntPtr copiedCapture)
    {
        if (handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(HomeMkvWriter));

        Check(k4a_record_write_capture(handle, copiedCapture), "k4a_record_write_capture");
        WrittenCount++;
    }

    // ------------------------------------------------------------
    // Capture copy
    // ------------------------------------------------------------
    // 骨格推定(トラッカー)に渡すキャプチャと画像のメモリを MKV の記録と共有すると、記録を閉じたときに
    // トラッカーのスレッドが解放済みのメモリを読んで Unity ごと落ちたため、MKV にはカラー・深度・赤外線を
    // 別のメモリにコピーしたキャプチャを渡す。コピーは記録用のスレッドで書いて解放する。
    public static IntPtr CopyCapture(Capture capture, out long colorTimestampUsec, out long depthTimestampUsec)
    {
        IntPtr source = GetNativeHandle(capture);
        Check(k4a_capture_create(out IntPtr copy), "k4a_capture_create");

        colorTimestampUsec = CopyImage(k4a_capture_get_color_image(source), copy, ImageSlot.Color);
        depthTimestampUsec = CopyImage(k4a_capture_get_depth_image(source), copy, ImageSlot.Depth);
        CopyImage(k4a_capture_get_ir_image(source), copy, ImageSlot.IR);

        return copy;
    }

    public static void ReleaseCapture(IntPtr copiedCapture)
    {
        if (copiedCapture != IntPtr.Zero)
            k4a_capture_release(copiedCapture);
    }

    private enum ImageSlot
    {
        Color,
        Depth,
        IR
    }

    // 画像をコピーして copy に入れる。元の画像の参照はここで解放する。デバイスのタイムスタンプ(μs)を返す(画像が無ければ -1)
    private static long CopyImage(IntPtr image, IntPtr copy, ImageSlot slot)
    {
        if (image == IntPtr.Zero)
            return -1;

        try
        {
            ulong size = (ulong)k4a_image_get_size(image);
            ulong timestamp = k4a_image_get_device_timestamp_usec(image);
            IntPtr buffer = Marshal.AllocHGlobal((IntPtr)(long)size);
            CopyMemory(buffer, k4a_image_get_buffer(image), (UIntPtr)size);

            int result = k4a_image_create_from_buffer(
                k4a_image_get_format(image),
                k4a_image_get_width_pixels(image),
                k4a_image_get_height_pixels(image),
                k4a_image_get_stride_bytes(image),
                buffer,
                (UIntPtr)size,
                FreeBufferCallback,
                IntPtr.Zero,
                out IntPtr copiedImage);

            if (result != Succeeded)
            {
                Marshal.FreeHGlobal(buffer);
                throw new InvalidOperationException($"k4a_image_create_from_buffer が失敗しました (k4a_result_t = {result})");
            }

            k4a_image_set_device_timestamp_usec(copiedImage, timestamp);
            k4a_image_set_system_timestamp_nsec(copiedImage, k4a_image_get_system_timestamp_nsec(image));

            // キャプチャが画像の参照を持つので、ここでの参照は解放する
            switch (slot)
            {
                case ImageSlot.Color: k4a_capture_set_color_image(copy, copiedImage); break;
                case ImageSlot.Depth: k4a_capture_set_depth_image(copy, copiedImage); break;
                default: k4a_capture_set_ir_image(copy, copiedImage); break;
            }

            k4a_image_release(copiedImage);
            return (long)timestamp;
        }
        finally
        {
            k4a_image_release(image);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MemoryDestroyCallback(IntPtr buffer, IntPtr context);

    // ネイティブ側から呼ばれるので、GC で消えないように static で持っておく
    private static readonly MemoryDestroyCallback FreeBufferCallback = (buffer, context) => Marshal.FreeHGlobal(buffer);

    public void Dispose()
    {
        if (handle == IntPtr.Zero)
            return;

        k4a_record_flush(handle);
        k4a_record_close(handle);
        handle = IntPtr.Zero;
    }

    private static void Check(int result, string function)
    {
        if (result != Succeeded)
            throw new InvalidOperationException($"{function} が失敗しました (k4a_result_t = {result})");
    }

    // Device / Capture の private フィールド handle (SafeHandle) からネイティブのポインタを取り出す
    private static IntPtr GetNativeHandle(object wrapper)
    {
        FieldInfo field = wrapper.GetType().GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic);

        if (!(field?.GetValue(wrapper) is SafeHandle safeHandle) || safeHandle.IsInvalid)
            throw new InvalidOperationException($"{wrapper.GetType().Name} のネイティブハンドルを取得できません。");

        return safeHandle.DangerousGetHandle();
    }

    // k4a_device_configuration_t (k4atypes.h)。bool は C の 1バイト。
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeConfiguration
    {
        public int colorFormat;
        public int colorResolution;
        public int depthMode;
        public int cameraFps;
        [MarshalAs(UnmanagedType.I1)] public bool synchronizedImagesOnly;
        public int depthDelayOffColorUsec;
        public int wiredSyncMode;
        public uint subordinateDelayOffMasterUsec;
        [MarshalAs(UnmanagedType.I1)] public bool disableStreamingIndicator;
    }

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int k4a_record_create(string path, IntPtr device, NativeConfiguration configuration, out IntPtr recordingHandle);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, BestFitMapping = false)]
    private static extern int k4a_record_add_tag(IntPtr recordingHandle, string name, string value);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_record_write_header(IntPtr recordingHandle);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_record_write_capture(IntPtr recordingHandle, IntPtr captureHandle);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_record_flush(IntPtr recordingHandle);

    [DllImport(RecordDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_record_close(IntPtr recordingHandle);

    // k4a.dll (キャプチャ/画像の操作)
    private const string SensorDll = "k4a";

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_capture_create(out IntPtr captureHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_capture_release(IntPtr captureHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr k4a_capture_get_color_image(IntPtr captureHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr k4a_capture_get_depth_image(IntPtr captureHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr k4a_capture_get_ir_image(IntPtr captureHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_capture_set_color_image(IntPtr captureHandle, IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_capture_set_depth_image(IntPtr captureHandle, IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_capture_set_ir_image(IntPtr captureHandle, IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_image_create_from_buffer(int format, int widthPixels, int heightPixels, int strideBytes,
        IntPtr buffer, UIntPtr bufferSize, MemoryDestroyCallback bufferReleaseCallback, IntPtr bufferReleaseContext, out IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_image_release(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr k4a_image_get_buffer(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern UIntPtr k4a_image_get_size(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_image_get_format(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_image_get_width_pixels(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_image_get_height_pixels(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int k4a_image_get_stride_bytes(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong k4a_image_get_device_timestamp_usec(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_image_set_device_timestamp_usec(IntPtr imageHandle, ulong timestampUsec);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong k4a_image_get_system_timestamp_nsec(IntPtr imageHandle);

    [DllImport(SensorDll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void k4a_image_set_system_timestamp_nsec(IntPtr imageHandle, ulong timestampNsec);

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr destination, IntPtr source, UIntPtr length);
}
