using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FilmBatch.Mobile.Probes;

// Shared macOS/iOS C API probe; no SDK bindings, codec implementation or product IO replacement.
public static unsafe partial class AppleImageIoProbe
{
    private const int Width = 51, Height = 37, Channels = 3;
    private const string Software = "FilmBatch Mobile Probe";
    private const string Description = "16-bit odd-width round trip";

    public static ProbeResult Run(string outputDirectory, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS() && !OperatingSystem.IsMacCatalyst())
            return new("apple-imageio", "skipped", "ImageIO requires an Apple platform; no replacement codec used.", []);
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var metrics = new Dictionary<string, string>();
        using var frameworks = new Frameworks();
        using var scope = new OwnedObjects();
        Directory.CreateDirectory(outputDirectory);
        var color = scope.Add(Native.CGColorSpaceCreateWithName(frameworks.Reference(frameworks.Graphics, "kCGColorSpaceSRGB")));
        var profile = scope.Add(Native.CGColorSpaceCopyICCData(color));
        var icc = Data(profile).ToArray();
        Require(icc.Length >= 128 && BinaryPrimitives.ReadUInt32BigEndian(icc) == icc.Length
            && icc.AsSpan(36, 4).SequenceEqual("acsp"u8) && icc.AsSpan(16, 4).SequenceEqual("RGB "u8), "Invalid RGB ICC profile.");
        metrics["IccSha256"] = Convert.ToHexStringLower(SHA256.HashData(icc));
        metrics["Fixture"] = $"{Width}x{Height} RGB16; orientation 3; no alpha";
        var pixels = new ushort[Width * Height * Channels];
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                for (var c = 0; c < Channels; c++) pixels[(y * Width + x) * Channels + c] = Expected(x, y, c);

        foreach (var format in new[] { "png", "tiff" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            RoundTrip(frameworks, pixels, color, icc, format,
                Path.Combine(outputDirectory, $"apple-imageio-{Width}x{Height}-rgb16.{format}"), metrics);
        }
        cancellationToken.ThrowIfCancellationRequested();
        metrics["ElapsedMilliseconds"] = timer.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture);
        return new("apple-imageio", "pass", "Real PNG/TIFF RGB16 encoding and ImageIO decoding preserved every component, ICC, geometry, orientation and format metadata.", metrics);
    }

    private static void RoundTrip(Frameworks frameworks, ushort[] pixels, nint color, byte[] icc,
        string format, string path, Dictionary<string, string> metrics)
    {
        using var scope = new OwnedObjects();
        nint data;
        fixed (ushort* pointer = pixels)
            data = scope.Add(Native.CFDataCreate(0, (nint)pointer, pixels.Length * sizeof(ushort)));
        // CFDataCreate copied the pinned bytes; provider/image retain their own native references.
        var provider = scope.Add(Native.CGDataProviderCreateWithCFData(data));
        var image = scope.Add(Native.CGImageCreate(Width, Height, 16, 48, Width * 6, color,
            BitConverter.IsLittleEndian ? 0x1000u : 0x3000u, provider, 0, 0, 0));
        var urlBytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path));
        var url = scope.Add(Native.CFURLCreateFromFileSystemRepresentation(0, urlBytes, urlBytes.Length, 0));
        var destination = scope.Add(Native.CGImageDestinationCreateWithURL(url, Text(scope, "public." + format), 1, 0));
        var isPng = format == "png";
        var formatMetadata = Dictionary(scope, frameworks,
            ("Software", Text(scope, Software)),
            (isPng ? "Description" : "ImageDescription", Text(scope, Description)));
        var metadata = Dictionary(scope, frameworks,
            ("Orientation", Number(scope, 3)), (isPng ? "{PNG}" : "{TIFF}", formatMetadata));
        Native.CGImageDestinationAddImage(destination, image, metadata);
        Require(Native.CGImageDestinationFinalize(destination) != 0, $"{format}: encode failed.");

        var source = scope.Add(Native.CGImageSourceCreateWithURL(url, 0));
        var properties = scope.Add(Native.CGImageSourceCopyPropertiesAtIndex(source, 0, 0));
        Require(Integer(properties, "PixelWidth", scope) == Width && Integer(properties, "PixelHeight", scope) == Height,
            $"{format}: metadata dimensions changed.");
        Require(Integer(properties, "Depth", scope) == 16, $"{format}: metadata bit depth changed.");
        Require(Integer(properties, "Orientation", scope) == 3, $"{format}: orientation metadata changed.");
        Require(ReadText(Get(properties, "ColorModel", scope)) == "RGB", $"{format}: color model changed.");
        var profileName = ReadText(Get(properties, "ProfileName", scope));
        Require(!string.IsNullOrEmpty(profileName), $"{format}: missing profile name.");
        var decodedMetadata = Get(properties, isPng ? "{PNG}" : "{TIFF}", scope);
        Require(ReadText(Get(decodedMetadata, "Software", scope)) == Software
            && ReadText(Get(decodedMetadata, isPng ? "Description" : "ImageDescription", scope)) == Description,
            $"{format}: software/description metadata changed.");
        var decoded = scope.Add(Native.CGImageSourceCreateImageAtIndex(source, 0, 0));
        Require(Native.CGImageGetWidth(decoded) == Width && Native.CGImageGetHeight(decoded) == Height,
            $"{format}: decoded dimensions changed.");
        Require(Native.CGImageGetBitsPerComponent(decoded) == 16 && Native.CGImageGetBitsPerPixel(decoded) == 48,
            $"{format}: expected decoded RGB16/48bpp.");
        var bitmapInfo = Native.CGImageGetBitmapInfo(decoded);
        Require((bitmapInfo & 0x1f) == 0, $"{format}: unexpected alpha/padding layout.");
        var order = bitmapInfo & 0x7000;
        Require(order is 0 or 0x1000 or 0x3000, $"{format}: unsupported native byte order {order:X}.");
        var stride = checked((int)Native.CGImageGetBytesPerRow(decoded));
        Require(stride >= Width * 6, $"{format}: invalid decoded stride.");
        var pixelData = scope.Add(Native.CGDataProviderCopyData(Native.CGImageGetDataProvider(decoded)));
        var bytes = Data(pixelData);
        Require(bytes.Length >= checked(stride * Height), $"{format}: short decoded buffer.");
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
                for (var c = 0; c < Channels; c++)
                {
                    var component = bytes.Slice(y * stride + (x * Channels + c) * 2, 2);
                    // CGImage default order is network/big-endian, not the host's CPU endianness.
                    var actual = order == 0x1000 ? BinaryPrimitives.ReadUInt16LittleEndian(component)
                        : BinaryPrimitives.ReadUInt16BigEndian(component);
                    if (actual != Expected(x, y, c))
                        throw new InvalidDataException($"{format}: pixel ({x},{y}) channel {c}: {actual} != {Expected(x, y, c)}.");
                }
        var decodedProfile = scope.Add(Native.CGColorSpaceCopyICCData(Native.CGImageGetColorSpace(decoded)));
        Require(Data(decodedProfile).SequenceEqual(icc), $"{format}: decoded ICC bytes differ from the encoded profile.");
        metrics[format + ".File"] = Path.GetFullPath(path);
        metrics[format + ".ComponentsChecked"] = pixels.Length.ToString(CultureInfo.InvariantCulture);
        metrics[format + ".BytesPerRow"] = stride.ToString(CultureInfo.InvariantCulture);
        metrics[format + ".BitmapInfo"] = bitmapInfo.ToString("X", CultureInfo.InvariantCulture);
        metrics[format + ".ProfileName"] = profileName;
        metrics[format + ".Metadata"] = "Orientation=3; Software and Description preserved";
        metrics[format + ".IccSha256"] = Convert.ToHexStringLower(SHA256.HashData(Data(decodedProfile)));
    }

    private static ushort Expected(int x, int y, int channel)
    {
        if (x == 0 && y == 0) return 0;
        if (x == Width - 1 && y == Height - 1) return ushort.MaxValue;
        if (x == 1 && y == 0) return channel switch { 0 => 1, 1 => 32768, _ => 65534 };
        return (ushort)((x * 7919 + y * 1049 + channel * 19001) & 0xffff);
    }

    private static nint Text(OwnedObjects scope, string value) => scope.Add(Native.CFStringCreateWithCString(0, value, 0x08000100));
    private static nint Number(OwnedObjects scope, int value) => scope.Add(Native.CFNumberCreate(0, 3, ref value));
    private static nint Dictionary(OwnedObjects scope, Frameworks frameworks, params (string Key, nint Value)[] values)
    {
        var keys = new nint[values.Length];
        var objects = new nint[values.Length];
        for (var i = 0; i < values.Length; i++) { keys[i] = Text(scope, values[i].Key); objects[i] = values[i].Value; }
        return scope.Add(Native.CFDictionaryCreate(0, keys, objects, keys.Length,
            NativeLibrary.GetExport(frameworks.Foundation, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(frameworks.Foundation, "kCFTypeDictionaryValueCallBacks")));
    }

    private static nint Get(nint dictionary, string key, OwnedObjects scope)
    {
        Require(dictionary != 0, $"Missing metadata dictionary for {key}.");
        var value = Native.CFDictionaryGetValue(dictionary, Text(scope, key));
        Require(value != 0, $"Missing metadata {key}.");
        return value;
    }

    private static long Integer(nint properties, string key, OwnedObjects scope)
    {
        var number = Get(properties, key, scope);
        Require(Native.CFGetTypeID(number) == Native.CFNumberGetTypeID(), $"Metadata {key} is not a number.");
        Require(Native.CFNumberGetValue(number, 4, out var result) != 0, $"Invalid number for {key}.");
        return result;
    }

    private static string ReadText(nint value)
    {
        Require(value != 0 && Native.CFGetTypeID(value) == Native.CFStringGetTypeID(), "Expected CFString metadata.");
        var buffer = new byte[checked((int)Native.CFStringGetLength(value) * 4 + 1)];
        fixed (byte* pointer = buffer)
            Require(Native.CFStringGetCString(value, (nint)pointer, buffer.Length, 0x08000100) != 0, "CFString UTF-8 conversion failed.");
        return Encoding.UTF8.GetString(buffer.AsSpan(0, Array.IndexOf(buffer, (byte)0)));
    }

    private static ReadOnlySpan<byte> Data(nint data)
    {
        Require(data != 0, "Expected native CFData.");
        var length = checked((int)Native.CFDataGetLength(data));
        var pointer = Native.CFDataGetBytePtr(data);
        Require(length >= 0 && (length == 0 || pointer != 0), "Invalid native CFData buffer.");
        return new ReadOnlySpan<byte>((void*)pointer, length);
    }

    private static void Require(bool condition, string error)
    {
        if (!condition) throw new InvalidDataException(error);
    }

    // Every Create/Copy reference is owned here. Get-rule pointers and constant symbols are borrowed.
    private sealed class OwnedObjects : IDisposable
    {
        private readonly List<nint> _objects = [];
        public nint Add(nint value)
        {
            Require(value != 0, "Apple native Create/Copy returned NULL.");
            _objects.Add(value);
            return value;
        }
        public void Dispose()
        {
            for (var i = _objects.Count - 1; i >= 0; i--) Native.CFRelease(_objects[i]);
        }
    }

    private sealed class Frameworks : IDisposable
    {
        public nint Foundation { get; private set; }
        public nint Graphics { get; private set; }
        private nint _imageIo;
        public Frameworks()
        {
            try
            {
                Foundation = NativeLibrary.Load(Native.Foundation);
                Graphics = NativeLibrary.Load(Native.Graphics);
                _imageIo = NativeLibrary.Load(Native.ImageIo);
            }
            catch { Dispose(); throw; }
        }
        // const CFxxxRef exports contain a pointer. Callback-struct exports instead use their address.
        public nint Reference(nint library, string symbol) => Marshal.ReadIntPtr(NativeLibrary.GetExport(library, symbol));
        public void Dispose()
        {
            if (_imageIo != 0) NativeLibrary.Free(_imageIo);
            if (Graphics != 0) NativeLibrary.Free(Graphics);
            if (Foundation != 0) NativeLibrary.Free(Foundation);
        }
    }

    // Explicit framework paths work on .NET macOS; '__Internal' is not a desktop dynamic library.
    // CFIndex -> nint, size_t -> nuint, CF Boolean/C bool -> byte. No delegate or pointer escapes a scope.
    private static partial class Native
    {
        internal const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        internal const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        internal const string ImageIo = "/System/Library/Frameworks/ImageIO.framework/ImageIO";
        [LibraryImport(Foundation, StringMarshalling = StringMarshalling.Utf8)]
        internal static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);
        [LibraryImport(Foundation)] internal static partial nint CFStringGetLength(nint value);
        [LibraryImport(Foundation)] internal static partial byte CFStringGetCString(nint value, nint buffer, nint size, uint encoding);
        [LibraryImport(Foundation)] internal static partial nint CFURLCreateFromFileSystemRepresentation(nint allocator, byte[] bytes, nint count, byte directory);
        [LibraryImport(Foundation)] internal static partial nint CFDataCreate(nint allocator, nint bytes, nint count);
        [LibraryImport(Foundation)] internal static partial nint CFDataGetLength(nint data);
        [LibraryImport(Foundation)] internal static partial nint CFDataGetBytePtr(nint data);
        [LibraryImport(Foundation)] internal static partial nint CFNumberCreate(nint allocator, nint type, ref int value);
        [LibraryImport(Foundation)] internal static partial byte CFNumberGetValue(nint number, nint type, out long value);
        [LibraryImport(Foundation)] internal static partial nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);
        [LibraryImport(Foundation)] internal static partial nint CFDictionaryGetValue(nint dictionary, nint key);
        [LibraryImport(Foundation)] internal static partial nint CFGetTypeID(nint value);
        [LibraryImport(Foundation)] internal static partial nint CFNumberGetTypeID();
        [LibraryImport(Foundation)] internal static partial nint CFStringGetTypeID();
        [LibraryImport(Foundation)] internal static partial void CFRelease(nint value);
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceCreateWithName(nint name);
        [LibraryImport(Graphics)] internal static partial nint CGColorSpaceCopyICCData(nint colorSpace);
        [LibraryImport(Graphics)] internal static partial nint CGDataProviderCreateWithCFData(nint data);
        [LibraryImport(Graphics)] internal static partial nint CGImageCreate(nuint width, nuint height, nuint bitsPerComponent, nuint bitsPerPixel, nuint bytesPerRow, nint colorSpace, uint bitmapInfo, nint provider, nint decode, byte interpolate, int intent);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetWidth(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetHeight(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetBitsPerComponent(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetBitsPerPixel(nint image);
        [LibraryImport(Graphics)] internal static partial nuint CGImageGetBytesPerRow(nint image);
        [LibraryImport(Graphics)] internal static partial uint CGImageGetBitmapInfo(nint image);
        [LibraryImport(Graphics)] internal static partial nint CGImageGetColorSpace(nint image);
        [LibraryImport(Graphics)] internal static partial nint CGImageGetDataProvider(nint image);
        [LibraryImport(Graphics)] internal static partial nint CGDataProviderCopyData(nint provider);
        [LibraryImport(ImageIo)] internal static partial nint CGImageDestinationCreateWithURL(nint url, nint type, nuint count, nint options);
        [LibraryImport(ImageIo)] internal static partial void CGImageDestinationAddImage(nint destination, nint image, nint properties);
        [LibraryImport(ImageIo)] internal static partial byte CGImageDestinationFinalize(nint destination);
        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCreateWithURL(nint url, nint options);
        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCopyPropertiesAtIndex(nint source, nuint index, nint options);
        [LibraryImport(ImageIo)] internal static partial nint CGImageSourceCreateImageAtIndex(nint source, nuint index, nint options);
    }
}
