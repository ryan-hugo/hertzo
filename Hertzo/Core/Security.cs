using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Hertzo.Core;

/// <summary>
/// Proteções do Hertzo contra abuso do fato de ele rodar como administrador.
/// O Hertzo lê arquivos que outros programas (sem admin) conseguem escrever; tudo o que vem
/// de lá é tratado como não confiável antes de virar escrita no registro, exclusão ou execução.
/// </summary>
public static partial class Guard
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    // ---------------------------------------------------------------------
    // %ProgramData%\Hertzo: só administradores escrevem
    // ---------------------------------------------------------------------

    /// <summary>
    /// Por padrão, qualquer usuário pode criar arquivos dentro de pastas novas em ProgramData. Como o
    /// diário de backup (journal.json) diz ao Hertzo o que escrever no registro ao desfazer, e o PresentMon
    /// é executado de lá, a pasta precisa ser só de administradores: dono = Administradores, permissões
    /// não herdadas, Usuários só leem.
    /// </summary>
    public static void HardenMachineData(string dir)
    {
        var di = new DirectoryInfo(dir);
        if (!di.Exists) di.Create();
        if (IsHardened(di)) return;

        Native.EnablePrivilege("SeTakeOwnershipPrivilege");
        Native.EnablePrivilege("SeRestorePrivilege");

        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(sec);

        // O que já estava lá dentro passa a ser dos Administradores e só herda as permissões novas.
        // Se algo não deixar nem isso (arquivo plantado com permissões fechadas), é apagado: não dá para confiar nele.
        foreach (var e in di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).OrderBy(x => x.FullName.Length))
        {
            try
            {
                if (e is DirectoryInfo sub)
                {
                    var s = new DirectorySecurity();
                    s.SetOwner(Admins);
                    sub.SetAccessControl(s);
                    ResetToInherited(sub.FullName);
                }
                else if (e is FileInfo f)
                {
                    var s = new FileSecurity();
                    s.SetOwner(Admins);
                    f.SetAccessControl(s);
                    ResetToInherited(f.FullName);
                }
            }
            catch
            {
                try { if (e is FileInfo f) f.Delete(); else ((DirectoryInfo)e).Delete(true); } catch { }
            }
        }
    }

    private static void ResetToInherited(string path)
    {
        // DACL vazia e não protegida = só as entradas herdadas da pasta do Hertzo.
        var empty = new RawAcl(GenericAcl.AclRevision, 0);
        var buf = new byte[empty.BinaryLength];
        empty.GetBinaryForm(buf, 0);
        var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            const int SE_FILE_OBJECT = 1;
            const uint DACL = 0x4, UNPROTECTED_DACL = 0x20000000;
            var rc = SetNamedSecurityInfo(path, SE_FILE_OBJECT, DACL | UNPROTECTED_DACL, IntPtr.Zero, IntPtr.Zero, h.AddrOfPinnedObject(), IntPtr.Zero);
            if (rc != 0) throw new UnauthorizedAccessException($"SetNamedSecurityInfo {rc}");
        }
        finally { h.Free(); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint SetNamedSecurityInfo(string name, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    private static bool IsHardened(DirectoryInfo di)
    {
        try
        {
            var sec = di.GetAccessControl();
            if (!sec.AreAccessRulesProtected) return false;
            if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier o || !(o == Admins || o == System)) return false;
            const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
                | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (r.AccessControlType != AccessControlType.Allow) continue;
                if (r.IdentityReference is SecurityIdentifier sid && (sid == Admins || sid == System)) continue;
                if ((r.FileSystemRights & write) != 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Arquivo só confiável se o dono for Administradores/SYSTEM (ninguém sem admin consegue ter criado).</summary>
    public static bool OwnedByAdmins(string path)
    {
        try
        {
            var o = new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return o == Admins || o == System;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------------
    // Diário: só restaura o que o próprio Hertzo escreveria
    // ---------------------------------------------------------------------

    private const string GpuPrefs = @"Software\Microsoft\DirectX\UserGpuPreferences";

    [GeneratedRegex(@"^SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options\\[^\\/:*?""<>|]+\.exe\\PerfOptions$", RegexOptions.IgnoreCase)]
    private static partial Regex IfeoRx();

    /// <summary>
    /// Uma entrada do diário só é aceita se o destino for exatamente um valor que aquele ajuste mexe.
    /// Assim, mesmo um journal.json adulterado não consegue fazer o Hertzo escrever em outro lugar do registro.
    /// </summary>
    public static bool IsAllowed(RegBackup b)
    {
        static bool Eq(string a, string c) => string.Equals(a, c, StringComparison.OrdinalIgnoreCase);
        if (b.Owner.StartsWith("game.gpu|", StringComparison.Ordinal))
            return b.Hive == Hive.HKCU && Eq(b.Path, GpuPrefs) && Eq(b.Name, b.Owner["game.gpu|".Length..]);
        if (b.Owner.StartsWith("game.fso|", StringComparison.Ordinal))
            return b.Hive == Hive.HKCU && Eq(b.Path, LayerTweak.LayersPath) && Eq(b.Name, b.Owner["game.fso|".Length..]);
        if (b.Owner.StartsWith("game.prio|", StringComparison.Ordinal))
            return b.Hive == Hive.HKLM && IfeoRx().IsMatch(b.Path) && Eq(b.Name, "CpuPriorityClass")
                   && b.Path.Contains(@"\" + b.Owner["game.prio|".Length..] + @"\", StringComparison.OrdinalIgnoreCase)
                   && (!b.Existed || b.Kind == Microsoft.Win32.RegistryValueKind.DWord);
        var t = TweakCatalog.Find(b.Owner);
        return t != null && t.Touches(b.Hive, b.Path, b.Name);
    }

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidRx();

    public static bool IsGuid(string? s) => s != null && GuidRx().IsMatch(s);

    // ---------------------------------------------------------------------
    // Caminhos
    // ---------------------------------------------------------------------

    public static string Full(string p) => Path.GetFullPath(p).TrimEnd('\\');

    /// <summary>True se <paramref name="path"/> está dentro de <paramref name="root"/> (sem truques de "..").</summary>
    public static bool IsUnder(string path, string root)
    {
        try
        {
            var p = Full(path);
            var r = Full(root) + "\\";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Um clipe que o Hertzo pode abrir/apagar: .mp4 que existe e não está em pasta do sistema.</summary>
    public static bool IsClipFile(string? path)
        => path != null && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) && File.Exists(path) && !IsSystemPath(path);

    /// <summary>Pastas onde o Hertzo nunca grava nem apaga arquivos do usuário.</summary>
    public static bool IsSystemPath(string path)
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        ];
        try
        {
            var p = Full(path);
            foreach (var r in roots.Where(r => r.Length > 0))
                if (p.Equals(Full(r), StringComparison.OrdinalIgnoreCase) || IsUnder(p, r)) return true;
            return Path.GetPathRoot(p)?.TrimEnd('\\').Equals(p, StringComparison.OrdinalIgnoreCase) == true; // raiz de disco
        }
        catch { return true; }
    }

    // ---------------------------------------------------------------------
    // Exclusão segura (Limpeza)
    // ---------------------------------------------------------------------

    /// <summary>
    /// Apaga um arquivo só se, no momento da exclusão, ele ainda estiver dentro de <paramref name="rootFinal"/>.
    /// Abre o próprio arquivo (sem seguir links) e confere o caminho real pelo identificador; assim, trocar uma
    /// subpasta por uma junção no meio da limpeza (truque clássico contra limpadores rodando como admin)
    /// não faz o Hertzo apagar nada fora da pasta alvo.
    /// </summary>
    public static bool SafeDelete(string file, string rootFinal)
    {
        const uint DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
        const uint SHARE_ALL = 0x7, OPEN_EXISTING = 3;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        using var h = CreateFile(file, DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) return false;

        var final = FinalPath(h);
        if (final == null || !final.StartsWith(rootFinal.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return false;

        // tira o "somente leitura" e marca para exclusão pelo próprio identificador
        var basic = new FILE_BASIC_INFO();
        if (GetFileInformationByHandleEx(h, 0, out basic, Marshal.SizeOf<FILE_BASIC_INFO>()) && (basic.FileAttributes & 0x1) != 0)
        {
            basic.FileAttributes &= ~0x1u;
            if (basic.FileAttributes == 0) basic.FileAttributes = 0x80;
            SetFileInformationByHandle(h, 0, ref basic, Marshal.SizeOf<FILE_BASIC_INFO>());
        }
        var disp = new FILE_DISPOSITION_INFO { DeleteFile = true };
        return SetFileInformationByHandle(h, 4, ref disp, Marshal.SizeOf<FILE_DISPOSITION_INFO>());
    }

    /// <summary>Caminho real (resolvendo junções) de uma pasta, no formato usado por <see cref="SafeDelete"/>.</summary>
    public static string? FinalDirectory(string dir)
    {
        const uint FILE_READ_ATTRIBUTES = 0x80, SHARE_ALL = 0x7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        using var h = CreateFile(dir, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        return h.IsInvalid ? null : FinalPath(h);
    }

    private static string? FinalPath(SafeFileHandle h)
    {
        var sb = new char[1024];
        var n = GetFinalPathNameByHandle(h, sb, (uint)sb.Length, 0);
        if (n == 0 || n >= sb.Length) return null;
        var s = new string(sb, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) return s[4..];
        return s;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO { [MarshalAs(UnmanagedType.U1)] public bool DeleteFile; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle h, char[] buf, uint len, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int cls, ref FILE_DISPOSITION_INFO info, int size);
}
