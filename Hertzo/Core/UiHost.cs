using System.Windows;

namespace Hertzo.Core;

public sealed class DialogRequest : ObservableObject
{
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Items { get; init; } = [];
    public string? CheckText { get; init; }
    private bool _check;
    public bool CheckValue { get => _check; set => Set(ref _check, value); }
    public string ConfirmText { get; init; } = "Confirmar";
    public string CancelText { get; init; } = "Cancelar";
    public bool Danger { get; init; }
    public int TimeoutSeconds { get; init; }
    private string _countdown = "";
    public string Countdown { get => _countdown; set => Set(ref _countdown, value); }

    public bool HasItems => Items.Count > 0;
    public bool HasCheck => CheckText != null;
    public bool HasCancel => CancelText.Length > 0;
    public TaskCompletionSource<bool> Result { get; } = new();
}

/// <summary>Ponte entre a lógica e a janela: diálogos, avisos, navegação e “ocupado”.</summary>
public static class UiHost
{
    public static Func<DialogRequest, Task<bool>>? ConfirmImpl;
    public static Action<string, Tone>? ToastImpl;
    public static Action<string>? NavigateImpl;
    public static Action<string?>? BusyImpl;

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    public static Task<bool> Confirm(DialogRequest r) => ConfirmImpl?.Invoke(r) ?? Task.FromResult(false);

    public static void Toast(string message, Tone tone = Tone.Ok) => OnUi(() => ToastImpl?.Invoke(message, tone));

    public static void Error(Exception ex)
    {
        Log(ex);
        Toast(ex.Message, Tone.Bad);
    }

    public static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.UserData, "erros.log"), $"[{DateTime.Now:s}] {ex}\n\n"); } catch { }
    }

    public static void Navigate(string key) => OnUi(() => NavigateImpl?.Invoke(key));

    public static async Task<T> Busy<T>(string message, Func<T> work)
    {
        OnUi(() => BusyImpl?.Invoke(message));
        try { return await Task.Run(work); }
        finally { OnUi(() => BusyImpl?.Invoke(null)); }
    }

    // ================= FLUXOS COMPARTILHADOS =================

    public static async Task ToggleTweak(Tweak t, bool on)
    {
        if (on && (t.Risk != Risk.Seguro) && t.HasWarning)
        {
            var ok = await Confirm(new DialogRequest
            {
                Title = t.Title,
                Message = t.Warning! + "\n\nVocê pode desfazer a qualquer momento por aqui ou na tela Backup.",
                ConfirmText = "Entendi, aplicar",
                Danger = t.Risk == Risk.Avancado,
            });
            if (!ok) { t.Refresh(); return; }
        }

        t.IsBusy = true;
        try
        {
            await Task.Run(() => { if (on) t.Apply(); else t.Revert(); });
            var msg = on ? $"Aplicado: {t.Title}" : $"Revertido: {t.Title}";
            if (t.RequiresReboot) msg += " · reinicie o PC para valer";
            Toast(msg, on ? Tone.Ok : Tone.Info);
        }
        catch (Exception ex)
        {
            Error(ex);
        }
        finally
        {
            await Task.Run(t.Refresh);
            t.IsBusy = false;
            AppEvents.RaiseStateChanged();
        }
    }

    public static async Task ApplyTweakFlow(string id)
    {
        var t = TweakCatalog.Find(id);
        if (t != null) await ToggleTweak(t, true);
    }

    public static async Task RunRecommended()
    {
        var pending = await Task.Run(() =>
        {
            foreach (var t in TweakCatalog.All) t.Refresh();
            return TweakCatalog.All.Where(t => t.Recommended && !t.IsApplied).ToList();
        });

        if (pending.Count == 0)
        {
            Toast("Todos os ajustes recomendados já estão aplicados.", Tone.Ok);
            return;
        }

        var req = new DialogRequest
        {
            Title = "Otimizar agora",
            Message = $"O Hertzo vai aplicar {pending.Count} ajuste(s) seguros e reversíveis. Nada de antivírus, Windows Update ou anti-cheat é alterado.",
            Items = pending.Select(t => t.Title).ToList(),
            CheckText = "Criar ponto de restauração do Windows antes (recomendado)",
            CheckValue = true,
            ConfirmText = "Aplicar ajustes",
        };
        if (!await Confirm(req)) return;

        if (req.CheckValue)
        {
            var (ok, msg) = await Busy("Criando ponto de restauração… isso pode levar até 1 minuto", () => RestorePoint.Create("Hertzo — antes de otimizar"));
            if (!ok)
            {
                var go = await Confirm(new DialogRequest
                {
                    Title = "Ponto de restauração não criado",
                    Message = msg + "\n\nOs ajustes continuam reversíveis pelo backup próprio do Hertzo. Deseja continuar mesmo assim?",
                    ConfirmText = "Continuar",
                });
                if (!go) return;
            }
        }

        var (done, errors, reboot) = await Busy("Aplicando ajustes…", () =>
        {
            int n = 0; bool rb = false; var errs = new List<string>();
            foreach (var t in pending)
            {
                try { t.Apply(); n++; rb |= t.RequiresReboot; }
                catch (Exception ex) { errs.Add($"{t.Title}: {ex.Message}"); Log(ex); }
            }
            return (n, errs, rb);
        });

        if (errors.Count == 0)
            Toast($"{done} ajuste(s) aplicados{(reboot ? " · reinicie o PC para valer tudo" : "")}.", Tone.Ok);
        else
            Toast($"{done} aplicados, {errors.Count} com erro. {errors[0]}", Tone.Warn);
        AppEvents.RaiseStateChanged();
    }

    public static async Task SetRefreshRateFlow(DisplayInfo d)
    {
        var go = await Confirm(new DialogRequest
        {
            Title = $"Mudar para {d.MaxHz} Hz",
            Message = $"A tela pode piscar por um instante. Se a imagem não voltar, não toque em nada: o Hertzo retorna para {d.CurrentHz} Hz sozinho em 15 segundos.",
            ConfirmText = "Mudar agora",
        });
        if (!go) return;

        var code = SystemInfo.SetRefreshRate(d.DeviceName, d.MaxHz);
        if (code != 0)
        {
            Toast($"O driver recusou {d.MaxHz} Hz (código {code}). Verifique o cabo (DisplayPort/HDMI 2.0+).", Tone.Bad);
            return;
        }

        var keep = await Confirm(new DialogRequest
        {
            Title = "Manter esta taxa?",
            Message = $"O monitor agora está em {d.MaxHz} Hz. A imagem está normal?",
            ConfirmText = "Manter",
            CancelText = "Voltar",
            TimeoutSeconds = 15,
        });

        if (!keep)
        {
            SystemInfo.SetRefreshRate(d.DeviceName, d.CurrentHz);
            Toast($"Voltamos para {d.CurrentHz} Hz.", Tone.Info);
        }
        else
        {
            Journal.Log($"Monitor: {d.CurrentHz} Hz → {d.MaxHz} Hz", "apply");
            Toast($"Monitor agora em {d.MaxHz} Hz. Confira também a taxa no painel da GPU.", Tone.Ok);
        }
        AppEvents.RaiseStateChanged();
    }
}
