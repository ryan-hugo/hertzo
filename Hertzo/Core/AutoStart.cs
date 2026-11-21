using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Hertzo.Core;

/// <summary>
/// Abrir o Hertzo em segundo plano ao entrar no Windows.
///
/// O Hertzo precisa de admin, e o Windows não abre programas com admin pela chave "Run" do registro. Por isso o
/// início automático é uma tarefa agendada ("Hertzo") que roda com privilégios elevados no logon do usuário.
///
/// Segurança: uma tarefa que roda como admin sem perguntar não pode apontar para um arquivo que qualquer programa
/// consegue trocar (Downloads, Área de Trabalho, a pasta onde você deixou o .exe). Então o Hertzo se copia para
/// %ProgramFiles%\Hertzo — onde só administradores escrevem — e a tarefa aponta para essa cópia.
/// </summary>
public static class AutoStart
{
    public const string TaskName = "Hertzo";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Hertzo");
    public static string InstalledExe => Path.Combine(InstallDir, "Hertzo.exe");

    private static string SchTasks => Shell.Sys("schtasks.exe");

    public static bool IsEnabled() => Shell.Run(SchTasks, $"/Query /TN \"{TaskName}\"", 15000).Code == 0;

    public static void Enable()
    {
        InstallCopy(force: true);
        var xmlPath = Path.Combine(AppPaths.MachineData, "tarefa-hertzo.xml"); // pasta só de admins: ninguém troca o XML no meio
        try
        {
            File.WriteAllText(xmlPath, TaskXml(), Encoding.Unicode);
            var (code, output) = Shell.Run(SchTasks, $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F", 30000);
            if (code != 0) throw new InvalidOperationException("O Agendador de Tarefas recusou criar o início automático: " + output.Trim());
        }
        finally { try { File.Delete(xmlPath); } catch { } }
        Journal.Log("Início automático com o Windows ligado (segundo plano)", "apply");
    }

    public static void Disable()
    {
        var (code, output) = Shell.Run(SchTasks, $"/Delete /TN \"{TaskName}\" /F", 30000);
        if (code != 0 && IsEnabled()) throw new InvalidOperationException("Não foi possível remover o início automático: " + output.Trim());
        // a cópia instalada só é apagada se não for ela que está rodando agora
        if (!IsRunningInstalledCopy())
            try { if (File.Exists(InstalledExe)) File.Delete(InstalledExe); if (Directory.Exists(InstallDir) && !Directory.EnumerateFileSystemEntries(InstallDir).Any()) Directory.Delete(InstallDir); } catch { }
        Journal.Log("Início automático com o Windows desligado", "revert");
    }

    /// <summary>Com o início automático ligado, um Hertzo mais novo aberto de outro lugar atualiza a cópia instalada.</summary>
    public static void RefreshInstalledCopy()
    {
        if (IsRunningInstalledCopy() || !File.Exists(InstalledExe) || !IsEnabled()) return;
        InstallCopy(force: false);
    }

    private static bool IsRunningInstalledCopy()
        => string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), InstalledExe, StringComparison.OrdinalIgnoreCase);

    private static void InstallCopy(bool force)
    {
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do Hertzo desconhecido.");
        if (!self.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsRunningInstalledCopy()) return;
        if (File.Exists(InstalledExe))
        {
            if (Hash(self) == Hash(InstalledExe)) return;
            if (!force && FileVer(self) < FileVer(InstalledExe)) return; // nunca troca por uma versão mais antiga sozinho
        }
        Directory.CreateDirectory(InstallDir);
        var tmp = InstalledExe + ".novo";
        File.Copy(self, tmp, true);
        File.Move(tmp, InstalledExe, true);
    }

    private static System.Version FileVer(string exe)
    {
        try { return System.Version.Parse(FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "0.0"); }
        catch { return new System.Version(0, 0); }
    }

    private static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    private static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    private static string TaskXml()
    {
        var id = WindowsIdentity.GetCurrent();
        var user = X(id.Name);
        var sid = X(id.User?.Value ?? id.Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Author>Hertzo</Author>
                <Description>Abre o Hertzo em segundo plano ao entrar no Windows, para o replay de clipes já estar pronto. Desligue em Hertzo › Clipes › Sempre ativo.</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT10S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{sid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{X(InstalledExe)}"</Command>
                  <Arguments>{Hertzo.Program.BackgroundArg}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }
}
