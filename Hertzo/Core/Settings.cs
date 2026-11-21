using System.Text;
using System.Text.Json;

namespace Hertzo.Core;

/// <summary>
/// Gravação de arquivos de configuração à prova de queda: escreve num .tmp, força para o disco e só então troca
/// pelo arquivo real, guardando a versão anterior em .bak. Se o PC desligar no meio, o arquivo antigo continua inteiro.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        var bytes = new UTF8Encoding(false).GetBytes(text);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, path + ".bak", true); return; }
            catch (IOException) { /* volume sem suporte a Replace: cai para Move */ }
        }
        File.Move(tmp, path, true);
    }

    /// <summary>
    /// Lê e interpreta o arquivo; se ele estiver corrompido, guarda uma cópia (.corrompido) e tenta o .bak.
    /// Devolve null só se não houver nenhuma versão válida — e registra o motivo no log, nunca em silêncio.
    /// </summary>
    public static T? Load<T>(string path, Func<string, T?> parse) where T : class
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            string text;
            try { text = ReadShared(candidate); }
            catch (Exception ex) { UiHost.Log(ex); continue; }
            try
            {
                var v = parse(text);
                if (v != null)
                {
                    if (candidate != path) UiHost.Log(new InvalidDataException($"{Path.GetFileName(path)} estava ilegível; configurações recuperadas da cópia de segurança."));
                    return v;
                }
            }
            catch (Exception ex)
            {
                UiHost.Log(new InvalidDataException($"{Path.GetFileName(candidate)} está corrompido; uma cópia foi guardada como .corrompido.", ex));
                try { File.Copy(candidate, candidate + ".corrompido", true); } catch { }
            }
        }
        return null;
    }

    /// <summary>Lê mesmo se outro processo estiver com o arquivo aberto, com algumas tentativas curtas.</summary>
    private static string ReadShared(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            catch (IOException) when (i < 4) { Thread.Sleep(60); }
        }
    }
}

/// <summary>Configurações gerais do app (não ligadas a uma tela): %LocalAppData%\Hertzo\app.json.</summary>
public sealed class AppSettings
{
    /// <summary>Fechar a janela deixa o Hertzo rodando na bandeja (o replay continua).</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Já mostrou o aviso “o Hertzo continua na bandeja”.</summary>
    public bool TrayHintShown { get; set; }

    private static readonly Lazy<AppSettings> _current = new(Load);
    public static AppSettings Current => _current.Value;

    private static string FilePath => Path.Combine(AppPaths.UserData, "app.json");

    private static AppSettings Load()
        => AtomicFile.Load(FilePath, t => JsonSerializer.Deserialize<AppSettings>(t)) ?? new AppSettings();

    public void Save()
    {
        try { AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception ex) { UiHost.Log(ex); }
    }
}
