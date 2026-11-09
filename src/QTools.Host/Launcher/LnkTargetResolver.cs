namespace QTools.Host.Launcher;

/// <summary>
/// 轻量 .lnk 目标解析：按 MS-SHLLINK 直接解析字节，取 LinkInfo 的 LocalBasePath。
/// 不走 COM（Native AOT 下不稳定）。解析失败（结构不认识、读文件异常）返回 null，
/// 调用方降级为按快捷方式名去重。
/// <para>
/// 实测（Win10/11 真机批量验证）：LinkInfo 固定头是 7 个 dword
/// （Size/HeaderSize/Flags/VolumeIDOffset/LocalBasePathOffset/CommonNetworkRelativeLinkOffset/CommonPathSuffixOffset），
/// 且无论头部 IsUnicode 标志，绝大多数写入器的 LocalBasePath 都是 ANSI 编码，少数是 UTF-16——
/// 按「每隔一字节是否为 0」的字节模式探测。路径只用于去重键，不用于启动/图标。
/// </para>
/// </summary>
public static class LnkTargetResolver
{
    private const int HeaderSizeMin = 76;
    private const uint LinkFlagsHasTargetIdList = 0x1;
    private const uint LinkFlagsHasLinkInfo = 0x2;
    private const uint LinkInfoFlagsHasLocalBasePath = 0x1;

    /// <summary>解析真实目标（如 C:\Program Files\Foo\foo.exe）；失败返回 null。</summary>
    public static string? TryResolveLocalBasePath(string lnkPath)
    {
        try
        {
            return TryParse(File.ReadAllBytes(lnkPath));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>字节级解析（公开便于单测）。</summary>
    public static string? TryParse(byte[] bytes)
    {
        if (bytes.Length < HeaderSizeMin ||
            bytes[0] != 0x4C || bytes[1] != 0x00 || bytes[2] != 0x00 || bytes[3] != 0x00)
        {
            return null;
        }

        var flags = BitConverter.ToUInt32(bytes, 20);
        var pos = HeaderSizeMin;

        if ((flags & LinkFlagsHasTargetIdList) != 0)
        {
            // IDListSize(2 字节，不含自身) + IDList（含 0 终止符）
            if (pos + 2 > bytes.Length) { return null; }
            var idListSize = BitConverter.ToUInt16(bytes, pos);
            if (idListSize == 0 || pos + 2 + idListSize > bytes.Length) { return null; }
            pos += 2 + idListSize;
        }

        if ((flags & LinkFlagsHasLinkInfo) == 0 || pos + 28 > bytes.Length)
        {
            return null;
        }

        var linkInfoSize = BitConverter.ToInt32(bytes, pos);
        var linkInfoFlags = BitConverter.ToUInt32(bytes, pos + 8);
        // 注意 +12 是 VolumeIDOffset，LocalBasePathOffset 在 +16
        var localPathOffset = BitConverter.ToInt32(bytes, pos + 16);

        if ((linkInfoFlags & LinkInfoFlagsHasLocalBasePath) == 0 ||
            linkInfoSize <= 0 ||
            pos + linkInfoSize > bytes.Length ||
            localPathOffset < 28 ||
            pos + localPathOffset >= bytes.Length)
        {
            return null;
        }

        var start = pos + localPathOffset;

        // UTF-16 的 ASCII 文本每隔一字节就是 0（"C:" -> 43 00 3A 00），ANSI 不可能
        if (start + 3 < bytes.Length && bytes[start + 1] == 0 && bytes[start + 3] == 0)
        {
            var end = start;
            while (end + 1 < bytes.Length && (bytes[end] != 0 || bytes[end + 1] != 0)) { end += 2; }
            if (end + 2 > bytes.Length) { return null; }
            return CleanPath(System.Text.Encoding.Unicode.GetString(bytes, start, end - start));
        }

        var stop = start;
        while (stop < bytes.Length && bytes[stop] != 0) { stop++; }
        // ANSI：中文 Windows 路径里的汉字是 GBK 双字节，Latin1 解码非 ASCII 部分会变形，
        // 但映射是确定性的（同一真实路径 -> 同一键），只用作去重键足够
        return CleanPath(System.Text.Encoding.Latin1.GetString(bytes, start, stop - start));
    }

    /// <summary>拒绝空串与含控制字符的解码结果（拦截错位解析出的垃圾）。</summary>
    private static string? CleanPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var c in value)
        {
            if (c < 0x20)
            {
                return null;
            }
        }

        return value;
    }
}
