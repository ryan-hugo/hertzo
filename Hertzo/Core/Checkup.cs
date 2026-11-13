namespace Hertzo.Core;

public sealed class CheckItem : ObservableObject
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required Tone Tone { get; init; }
    public string Group { get; init; } = "";
    public int Weight { get; init; }
    public string? ActionLabel { get; init; }
    public Func<Task>? Action { get; init; }
    public bool HasAction => Action != null && ActionLabel != null;

    public string StatusLabel => Tone switch
    {
        Tone.Ok => "OK",
        Tone.Warn => "ATENÇÃO",
        Tone.Bad => "PROBLEMA",
        _ => "INFO",
    };

    public bool IsIssue => Tone is Tone.Warn or Tone.Bad;
}

/// <summary>
/// Diagnóstico. Prioriza o que mais pesa de verdade no desempenho: taxa de atualização errada,
/// RAM em canal único / sem XMP, plano de energia, gravação em segundo plano e drivers velhos.
/// </summary>
public static class Checkup
{
    public static List<CheckItem> Run(HardwareSnapshot hw)
    {
        var list = new List<CheckItem>();

        // ---------- Monitor ----------
        foreach (var d in SystemInfo.Displays())
        {
            var label = d.IsPrimary ? "Monitor principal" : d.MonitorName;
            if (d.BelowMax)
                list.Add(new CheckItem
                {
                    Group = "Monitor",
                    Title = $"{label} em {d.CurrentHz} Hz — ele suporta {d.MaxHz} Hz",
                    Detail = "É o erro mais comum e o mais caro: você paga por um monitor rápido e o Windows usa a taxa mais baixa. Corrigir é instantâneo e reversível.",
                    Tone = Tone.Bad,
                    Weight = 25,
                    ActionLabel = $"Usar {d.MaxHz} Hz",
                    Action = () => UiHost.SetRefreshRateFlow(d),
                });
            else
                list.Add(new CheckItem
                {
                    Group = "Monitor",
                    Title = $"{label} na taxa máxima ({d.CurrentHz} Hz)",
                    Detail = $"{d.Width}×{d.Height} a {d.CurrentHz} Hz.",
                    Tone = Tone.Ok,
                    Weight = 25,
                });
        }

        // ---------- Memória ----------
        if (hw.Modules.Count > 0)
        {
            var desktop = !hw.IsLaptop;
            if (hw.Modules.Count == 1 && desktop)
                list.Add(new CheckItem
                {
                    Group = "Memória",
                    Title = "Memória RAM em canal único (1 pente)",
                    Detail = "Com um só pente, a banda de memória cai pela metade. Em jogos limitados pela CPU (VALORANT, CS2, Fortnite) isso costuma custar bastante FPS. Dois pentes iguais resolvem.",
                    Tone = Tone.Warn,
                    Weight = 10,
                });
            else
                list.Add(new CheckItem
                {
                    Group = "Memória",
                    Title = $"{hw.Modules.Count} módulo(s) de RAM",
                    Detail = hw.IsLaptop ? "Notebook: a configuração de memória depende do modelo." : "Configuração compatível com dual-channel.",
                    Tone = Tone.Ok,
                    Weight = 10,
                });

            var mhz = hw.RamMHz;
            var type = hw.RamType;
            bool jedec = desktop && ((type == "DDR4" && mhz > 0 && mhz <= 2666) || (type == "DDR5" && mhz > 0 && mhz <= 4800));
            if (jedec)
                list.Add(new CheckItem
                {
                    Group = "Memória",
                    Title = $"RAM {type} rodando a {mhz} MT/s — provável XMP/EXPO desligado",
                    Detail = "Esta é a velocidade padrão (JEDEC). Quase todo pente gamer é vendido para rodar mais rápido, mas isso só liga na BIOS: procure “XMP” (Intel) ou “EXPO/DOCP” (AMD) e ative o perfil 1. É seguro e oficial.",
                    Tone = Tone.Warn,
                    Weight = 10,
                });
            else if (mhz > 0)
                list.Add(new CheckItem
                {
                    Group = "Memória",
                    Title = $"RAM {type} a {mhz} MT/s",
                    Detail = "Velocidade acima do padrão JEDEC ou notebook — nada a fazer.",
                    Tone = Tone.Ok,
                    Weight = 10,
                });

            var gb = hw.RamTotal / 1024.0 / 1024 / 1024;
            if (gb < 15)
                list.Add(new CheckItem
                {
                    Group = "Memória",
                    Title = $"{gb:0} GB de RAM",
                    Detail = "Jogos atuais e o Discord/navegador juntos passam fácil de 8 GB. Com pouca RAM, use a limpeza da memória em espera antes de jogar e feche o navegador.",
                    Tone = Tone.Warn,
                    Weight = 5,
                    ActionLabel = "Abrir limpeza",
                    Action = () => { UiHost.Navigate("cleanup"); return Task.CompletedTask; },
                });
        }

        // ---------- Energia ----------
        var active = Power.Active();
        var onBattery = SystemInfo.OnBattery();
        if (onBattery)
            list.Add(new CheckItem
            {
                Group = "Energia",
                Title = "Notebook na bateria",
                Detail = "Na bateria, CPU e GPU são limitadas pelo próprio hardware. Para jogar, conecte o carregador.",
                Tone = Tone.Warn,
                Weight = 5,
            });
        if (Power.IsPerformancePlan(active))
            list.Add(new CheckItem { Group = "Energia", Title = $"Plano de energia: {Power.ActiveName()}", Detail = "Plano de alto desempenho ativo.", Tone = Tone.Ok, Weight = 10 });
        else if (active != null && active != Power.Balanced && active != Power.Saver)
            list.Add(new CheckItem { Group = "Energia", Title = $"Plano de energia: {Power.ActiveName()}", Detail = "Plano personalizado (de fabricante ou criado por você). O Hertzo não mexe nele.", Tone = Tone.Ok, Weight = 10 });
        else
            list.Add(TweakCheck("sys.power.high", "Energia", $"Plano de energia: {Power.ActiveName()}",
                "O plano Equilibrado reduz o clock em momentos de pouca carga e pode piorar os 1% low.", Tone.Warn, 10));

        // ---------- Windows ----------
        AddTweakCheck(list, "sys.gamemode", "Windows", "Modo de Jogo do Windows", "Ligado.", "Desligado — o Windows Update pode atrapalhar durante a partida.", Tone.Bad, 6);
        AddTweakCheck(list, "gfx.gamedvr", "Windows", "Gravação contínua do Xbox Game Bar", "Desligada.", "Ligada — usa o codificador de vídeo da GPU o tempo todo.", Tone.Warn, 8);
        AddTweakCheck(list, "in.mouseaccel", "Windows", "Aceleração do mouse", "Desligada.", "Ligada (“Aumentar precisão do ponteiro”) — atrapalha a memória muscular.", Tone.Warn, 8);
        if (hw.IsWin11)
            AddTweakCheck(list, "gfx.windowed", "Windows", "Otimizações para jogos em janela", "Ligadas.", "Desligadas — jogos DX11 em janela sem bordas têm mais latência.", Tone.Warn, 6);

        // ---------- GPU ----------
        var gpu = hw.MainGpu;
        if (gpu?.DriverDate is DateTime dd)
        {
            var days = (DateTime.Now - dd).TotalDays;
            var url = gpu.Vendor switch
            {
                "NVIDIA" => "https://www.nvidia.com/pt-br/drivers/",
                "AMD" => "https://www.amd.com/pt/support/download/drivers.html",
                "Intel" => "https://www.intel.com.br/content/www/br/pt/download-center/home.html",
                _ => null,
            };
            if (days > 180)
                list.Add(new CheckItem
                {
                    Group = "GPU",
                    Title = $"Driver de vídeo de {dd:MM/yyyy} ({days / 30:0} meses)",
                    Detail = "Drivers novos trazem otimizações para jogos recentes e correções de travadas. Se a sua placa é de gerações antigas (ex.: RX 400/500, GTX 900), o fabricante pode ter encerrado as atualizações — nesse caso, o seu já é o mais recente.",
                    Tone = Tone.Warn,
                    Weight = 7,
                    ActionLabel = url != null ? "Site do fabricante" : null,
                    Action = url != null ? () => { Shell.OpenUrl(url); return Task.CompletedTask; } : null,
                });
            else
                list.Add(new CheckItem { Group = "GPU", Title = $"Driver de vídeo recente ({dd:dd/MM/yyyy})", Detail = $"{gpu.Name} · versão {gpu.DriverVersion}", Tone = Tone.Ok, Weight = 7 });
        }

        var hags = Reg.GetDword(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        list.Add(new CheckItem
        {
            Group = "GPU",
            Title = hags == 2 ? "HAGS ligado" : "HAGS desligado",
            Detail = hags == 2
                ? "Necessário para DLSS Frame Generation. Se notar micro-travadas, teste desligar em Otimizações."
                : "Ganho pequeno na maioria dos jogos; obrigatório só para DLSS Frame Generation.",
            Tone = Tone.Info,
        });

        // ---------- Segurança × desempenho ----------
        if (hw.VbsRunning == true)
            list.Add(new CheckItem
            {
                Group = "Segurança",
                Title = hw.HvciRunning == true ? "VBS e Integridade de Memória ativos" : "VBS ativo",
                Detail = "Custa até ~10% de FPS em jogos limitados pela CPU, mas é exigido pela FACEIT e pelo modo On-Demand do Vanguard. O Hertzo não mexe nisso por padrão — veja Otimizações › Avançado.",
                Tone = Tone.Info,
            });
        else if (hw.VbsRunning == false)
            list.Add(new CheckItem
            {
                Group = "Segurança",
                Title = "VBS desativado",
                Detail = "Melhor para FPS, porém a FACEIT exige VBS e o Vanguard On-Demand também.",
                Tone = Tone.Info,
            });

        // ---------- Armazenamento ----------
        var (free, total) = SystemInfo.SystemDrive();
        if (total > 0)
        {
            var pct = (double)free / total;
            list.Add(new CheckItem
            {
                Group = "Armazenamento",
                Title = $"Disco do sistema: {SystemInfo.Bytes(free)} livres ({pct:P0})",
                Detail = pct < 0.10 ? "Pouco espaço deixa o Windows lento e impede atualizações de jogos."
                       : pct < 0.20 ? "Ok, mas vale liberar espaço antes de grandes atualizações." : "Espaço confortável.",
                Tone = pct < 0.10 ? Tone.Bad : pct < 0.20 ? Tone.Warn : Tone.Ok,
                Weight = 5,
                ActionLabel = pct < 0.20 ? "Abrir limpeza" : null,
                Action = pct < 0.20 ? () => { UiHost.Navigate("cleanup"); return Task.CompletedTask; } : null,
            });
        }

        if (!hw.IsWin11)
            list.Add(new CheckItem
            {
                Group = "Windows",
                Title = "Windows 10",
                Detail = "O suporte do Windows 10 terminou em outubro de 2025 e o Vanguard On-Demand exige Windows 11 25H2+. Alguns ajustes do Hertzo só existem no Windows 11.",
                Tone = Tone.Info,
            });

        return list;
    }

    public static int Score(IEnumerable<CheckItem> items)
    {
        double total = 0, earned = 0;
        foreach (var i in items.Where(i => i.Weight > 0))
        {
            total += i.Weight;
            earned += i.Tone switch { Tone.Ok => i.Weight, Tone.Warn => i.Weight * 0.4, Tone.Bad => 0, _ => i.Weight };
        }
        return total == 0 ? 100 : (int)Math.Round(earned / total * 100);
    }

    private static void AddTweakCheck(List<CheckItem> list, string id, string group, string title, string okText, string badText, Tone badTone, int weight)
    {
        var t = TweakCatalog.Find(id);
        if (t == null) return;
        t.Refresh();
        if (t.IsApplied)
            list.Add(new CheckItem { Group = group, Title = title, Detail = okText, Tone = Tone.Ok, Weight = weight });
        else
            list.Add(TweakCheck(id, group, title, badText, badTone, weight));
    }

    private static CheckItem TweakCheck(string id, string group, string title, string detail, Tone tone, int weight) => new()
    {
        Group = group,
        Title = title,
        Detail = detail,
        Tone = tone,
        Weight = weight,
        ActionLabel = "Corrigir",
        Action = () => UiHost.ApplyTweakFlow(id),
    };
}
