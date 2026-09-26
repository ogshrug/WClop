namespace WClop.Core.Images;

/// <summary>Base for expected outcomes that stop a job. The original is always left intact.</summary>
public abstract class OptimisationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The output wasn't smaller. Pipelines treat this as "already fully compressed", not an error (§8.1).</summary>
public sealed class NotSmallerException(long originalSize, long outputSize)
    : OptimisationException("File already fully compressed")
{
    public long OriginalSize { get; } = originalSize;
    public long OutputSize { get; } = outputSize;
}

public sealed class AlreadyOptimisedException(string path) : OptimisationException("File was already optimised")
{
    public string Path { get; } = path;
}

public sealed class UnsupportedFormatException(string message) : OptimisationException(message);

/// <summary>An animated input came out as a single frame, e.g. a truncated download (§8.4).</summary>
public sealed class AnimationFlattenedException(int inputFrames, int outputFrames)
    : OptimisationException($"Animation would be flattened ({inputFrames} frames in, {outputFrames} out)");

public sealed class OutputUnreadableException(string message, Exception? inner = null)
    : OptimisationException(message, inner);
