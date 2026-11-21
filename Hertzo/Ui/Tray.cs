using System.Windows;
using Hertzo.Core;
using Hertzo.ViewModels;
using Forms = System.Windows.Forms;

namespace Hertzo.Ui;

/// <summary>Ícone na bandeja (perto do relógio): abrir, salvar o replay, ligar/desligar o replay e sair.</summary>
public static class Tray
{
    private static Forms.NotifyIcon? _icon;
    private static Forms.ToolStripMenuItem? _status, _toggle, _save;

    public static void Init(Window main)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        _status = new Forms.ToolStripMenuItem("Hertzo") { Enabled = false };
        var open = new Forms.ToolStripMenuItem("Abrir o Hertzo", null, (_, _) => Show()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) };
        _save = new Forms.ToolStripMenuItem("Salvar replay agora", null, (_, _) => OnUi(() => ClipsViewModel.Current?.SaveReplayNow()));
        _toggle = new Forms.ToolStripMenuItem("Ligar replay", null, (_, _) => OnUi(() => ClipsViewModel.Current?.ToggleReplay()));
        var exit = new Forms.ToolStripMenuItem("Sair do Hertzo", null, (_, _) => OnUi(App.ExitApp));
        menu.Items.AddRange([_status, new Forms.ToolStripSeparator(), open, _save, _toggle, new Forms.ToolStripSeparator(), exit]);
        menu.Opening += (_, _) => Refresh();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Hertzo",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Show(); };
        _icon.BalloonTipClicked += (_, _) => Show();
    }

    private static void Show() => OnUi(() => (Application.Current as App)?.ShowMainWindow());

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    private static void Refresh()
    {
        var vm = ClipsViewModel.Current;
        if (vm == null || _status == null || _toggle == null || _save == null) return;
        _status.Text = vm.TrayStatus;
        _toggle.Text = vm.Armed ? "Desligar replay" : "Ligar replay";
        _save.Enabled = vm.IsRecording;
        _save.Text = vm.IsRecording ? $"Salvar replay agora ({vm.HotkeyLabel})" : "Salvar replay agora";
        if (_icon != null) _icon.Text = Trim("Hertzo · " + vm.TrayStatus);
    }

    private static string Trim(string s) => s.Length > 63 ? s[..63] : s; // limite do Windows para a dica do ícone

    /// <summary>Na primeira vez que a janela vai para a bandeja, avisa onde o Hertzo foi parar.</summary>
    public static void HintOnce()
    {
        if (_icon == null || AppSettings.Current.TrayHintShown) return;
        AppSettings.Current.TrayHintShown = true;
        AppSettings.Current.Save();
        _icon.ShowBalloonTip(5000, "O Hertzo continua aberto aqui",
            "O replay e os atalhos seguem funcionando. Clique no ícone para abrir; para fechar de vez, use “Sair do Hertzo”.",
            Forms.ToolTipIcon.Info);
    }

    public static void Dispose()
    {
        if (_icon == null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/hertzo.ico"))?.Stream;
            if (s != null) return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
        }
        catch { }
        try { return System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application; }
        catch { return System.Drawing.SystemIcons.Application; }
    }
}
