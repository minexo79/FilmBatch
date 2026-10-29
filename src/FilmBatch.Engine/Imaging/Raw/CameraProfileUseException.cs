namespace FilmBatch.Engine.Imaging.Raw;

/// <summary>所選相機描述檔不適用於此來源；不得回退到自動外觀。</summary>
public enum CameraProfileUseFault
{
    MissingModel,
    ModelMismatch,
    Policy,
}

/// <summary>相機描述檔機型或使用政策不符。</summary>
public sealed class CameraProfileUseException(CameraProfileUseFault fault, string message) : Exception(message)
{
    public CameraProfileUseFault Fault { get; } = fault;
}
