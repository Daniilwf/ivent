using GameEvent.Engine.Kernel;
using NetVips;

namespace GameEvent.Infrastructure.Files;

/// <summary>Upload limits (D-108), from the <c>Files</c> section of the configuration.</summary>
public sealed record FileLimits
{
    /// <summary>The largest upload of any kind.</summary>
    public long MaxUploadBytes { get; init; } = 15L * 1024 * 1024;

    /// <summary>The largest GIF: kept as it is, so its own size counts (SPEC «Файлы»).</summary>
    public long MaxGifBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>A still image's longer side after re-encoding.</summary>
    public int MaxLongSide { get; init; } = 2560;

    /// <summary>Width × height × frames of the decoded image: a small file that unpacks into gigabytes is refused.</summary>
    public long MaxPixels { get; init; } = 100_000_000;

    /// <summary>The longer side of the thumbnail for the map and lists.</summary>
    public int ThumbnailSize { get; init; } = 256;

    public int WebpQuality { get; init; } = 82;

    /// <summary>Uploads per user in 24 hours.</summary>
    public int UploadsPerDay { get; init; } = 60;
}

/// <summary>An upload after processing: the file to store, its thumbnail and what it is.</summary>
public sealed record ProcessedFile(string MediaType, byte[] Main, byte[] Thumbnail, int Width, int Height, int Frames);

/// <summary>
/// Turns an upload into what is stored (SPEC «Файлы», «Трудности реализации», D-25, D-108). The type is decided by the
/// content — JPEG, PNG, WebP or GIF, nothing else reaches the decoder. A still image is re-encoded to WebP no larger than
/// <see cref="FileLimits.MaxLongSide"/>, turned by its orientation and stripped of metadata (a photo's location stays with
/// the player). A GIF is kept byte for byte within <see cref="FileLimits.MaxGifBytes"/>; its thumbnail stays animated.
/// </summary>
public static class ImageProcessor
{
    public const string TypeInvalid = "file.typeInvalid";
    public const string TooLarge = "file.tooLarge";
    public const string TooManyPixels = "file.tooManyPixels";
    public const string Broken = "file.broken";

    static ImageProcessor()
    {
        // Only the decoders libvips marks as safe for untrusted input; no operation cache of users' pictures
        NetVips.NetVips.BlockUntrusted = true;
        Cache.Max = 0;
    }

    private enum Kind
    {
        Jpeg,
        Png,
        Webp,
        Gif,
    }

    public static (ProcessedFile? File, Rejection? Rejection) Process(byte[] input, FileLimits limits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(limits);
        if (input.LongLength > limits.MaxUploadBytes)
        {
            return (null, new Rejection(TooLarge, $"A file is at most {limits.MaxUploadBytes / 1024 / 1024} MB."));
        }

        if (Sniff(input) is not { } kind)
        {
            return (null, new Rejection(TypeInvalid, "Only JPEG, PNG, WebP and GIF pictures are accepted."));
        }

        if (kind == Kind.Gif && input.LongLength > limits.MaxGifBytes)
        {
            return (null, new Rejection(TooLarge, $"A GIF is at most {limits.MaxGifBytes / 1024 / 1024} MB."));
        }

        try
        {
            return kind == Kind.Gif ? Gif(input, limits) : Still(input, kind, limits);
        }
        catch (VipsException)
        {
            return (null, new Rejection(Broken, "The picture cannot be read."));
        }
    }

    private static (ProcessedFile?, Rejection?) Still(byte[] input, Kind kind, FileLimits limits)
    {
        // The header only: the size is checked before anything is decoded
        using (var header = Image.NewFromBuffer(input, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error))
        {
            if (Mismatch(header, kind) is { } mismatch)
            {
                return (null, mismatch);
            }

            if ((long)header.Width * header.Height > limits.MaxPixels)
            {
                return (null, new Rejection(TooManyPixels, $"A picture has at most {limits.MaxPixels} pixels."));
            }
        }

        using var main = Image.ThumbnailBuffer(input, limits.MaxLongSide, height: limits.MaxLongSide, size: Enums.Size.Down, failOn: Enums.FailOn.Error);
        using var thumbnail = Image.ThumbnailBuffer(input, limits.ThumbnailSize, height: limits.ThumbnailSize, size: Enums.Size.Down, failOn: Enums.FailOn.Error);
        return (new ProcessedFile(
            FileNames.Webp,
            main.WebpsaveBuffer(q: limits.WebpQuality, keep: Enums.ForeignKeep.None),
            thumbnail.WebpsaveBuffer(q: limits.WebpQuality, keep: Enums.ForeignKeep.None),
            main.Width,
            main.Height,
            1), null);
    }

    private static (ProcessedFile?, Rejection?) Gif(byte[] input, FileLimits limits)
    {
        int width, height, frames;
        using (var header = Image.NewFromBuffer(input, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error, kwargs: new VOption { { "n", -1 } }))
        {
            if (Mismatch(header, Kind.Gif) is { } mismatch)
            {
                return (null, mismatch);
            }

            width = header.Width;
            height = header.PageHeight;
            frames = header.Contains("n-pages") ? (int)header.Get("n-pages") : 1;
            if ((long)width * height * frames > limits.MaxPixels)
            {
                return (null, new Rejection(TooManyPixels, $"A GIF has at most {limits.MaxPixels} pixels in all its frames."));
            }

            // Decoding every frame once proves the whole file readable before it is kept as it is
            using var decoded = header.Copy();
            _ = decoded.Avg();
        }

        using var thumbnail = Image.ThumbnailBuffer(
            input, limits.ThumbnailSize, optionString: "[n=-1]", height: limits.ThumbnailSize, size: Enums.Size.Down, failOn: Enums.FailOn.Error);
        return (new ProcessedFile(FileNames.Gif, input, thumbnail.GifsaveBuffer(keep: Enums.ForeignKeep.None), width, height, frames), null);
    }

    /// <summary>The decoder libvips picked must be the one the first bytes promised: no polyglot file slips through.</summary>
    private static Rejection? Mismatch(Image image, Kind kind)
    {
        var loader = image.Contains("vips-loader") ? (string)image.Get("vips-loader") : "";
        var expected = kind switch
        {
            Kind.Jpeg => "jpegload",
            Kind.Png => "pngload",
            Kind.Webp => "webpload",
            _ => "gifload",
        };
        return loader.StartsWith(expected, StringComparison.Ordinal)
            ? null
            : new Rejection(TypeInvalid, "Only JPEG, PNG, WebP and GIF pictures are accepted.");
    }

    private static Kind? Sniff(ReadOnlySpan<byte> data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => Kind.Jpeg,
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, ..] => Kind.Png,
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'7' or (byte)'9', (byte)'a', ..] => Kind.Gif,
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => Kind.Webp,
        _ => null,
    };
}
