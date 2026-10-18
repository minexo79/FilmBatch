namespace FilmBatch.Raw.Opcodes;

/// <summary>
/// DNG 運算碼（OpcodeList1／2／3 的一項）。具體運算碼（WarpRectilinear、GainMap、MapTable…）在 Develop 切片中以
/// 衍生 record 實作，解析與套用由同一切片負責。
/// </summary>
public abstract partial record Opcode;

/// <summary>DNG 的三個運算碼清單：1 作用於原始值、2 作用於正規化後、3 作用於去馬賽克後的相機 RGB。</summary>
public sealed class OpcodeLists
{
    public List<Opcode> List1 { get; init; } = [];

    public List<Opcode> List2 { get; init; } = [];

    public List<Opcode> List3 { get; init; } = [];

    public OpcodeLists Clone() => new() { List1 = [.. List1], List2 = [.. List2], List3 = [.. List3] };
}
