namespace FilmBatch.Gpu.Device;

public sealed class GpuException(string message, Exception? innerException = null) : Exception(message, innerException);
