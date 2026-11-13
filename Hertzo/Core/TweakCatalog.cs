using Microsoft.Win32;

namespace Hertzo.Core;

/// <summary>
/// Catálogo de ajustes. Critério de inclusão: documentado pela Microsoft/fabricantes OU amplamente testado,
/// sempre reversível, sem desativar proteções por padrão. Rótulos de evidência são honestos —
/// ajustes com evidência fraca ficam disponíveis mas NUNCA entram no perfil recomendado.
/// </summary>
public static class TweakCatalog
{
    public const string CatSistema = "Sistema e energia";
    public const string CatGraficos = "Gráficos e exibição";
    public const string CatEntrada = "Mouse e teclado";
    public const string CatSegundoPlano = "Segundo plano e privacidade";
    public const string CatRede = "Rede";
    public const string CatAvancado = "Avançado";

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    private const string MmPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmGames = MmPath + @"\Tasks\Games";

    // powercfg: SUB_USB / USBSELECTIVESUSPEND
    private static readonly Guid UsbSub = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    private static RegistryValueKind DW => RegistryValueKind.DWord;
    private static RegistryValueKind SZ => RegistryValueKind.String;

    private static List<Tweak>? _all;
    public static IReadOnlyList<Tweak> All => _all ??= Build().Where(t => t.IsAvailable).ToList();

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    private static IEnumerable<Tweak> Build()
    {
        // ================= SISTEMA =================
        yield return new PowerPlanTweak
        {
            Id = "sys.power.high",
            Category = CatSistema,
            Title = "Plano de energia Alto Desempenho",
            Description = "Mantém a CPU pronta para subir o clock na hora, sem a latência de “acordar” dos núcleos. Ajuda principalmente nos 1% low em jogos competitivos.",
            BaseGuid = Power.High,
            ExtraKey = "power.high",
            Impact = Impact.Medio,
            Evidence = Evidence.Moderada,
            Recommended = true,
            Warning = "Em notebook na bateria, gasta mais energia. Use na tomada.",
        };

        yield return new RegistryTweak
        {
            Id = "sys.gamemode",
            Category = CatSistema,
            Title = "Modo de Jogo do Windows",
            Description = "Recurso oficial que prioriza o jogo em primeiro plano e impede o Windows Update de instalar drivers e reiniciar durante a partida.",
            Ops =
            [
                new(Hive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", DW, 1, 0, MissingMatches: true),
                new(Hive.HKCU, @"Software\Microsoft\GameBar", "AllowAutoGameMode", DW, 1, null, MissingMatches: true),
            ],
            Impact = Impact.Baixo,
            Evidence = Evidence.Moderada,
            Recommended = true,
        };

        yield return new RegistryTweak
        {
            Id = "sys.mmcss",
            Category = CatSistema,
            Title = "Prioridade de jogos no agendador multimídia",
            Description = "Define a tarefa “Games” do MMCSS com prioridade alta de CPU, GPU e E/S. Só tem efeito em jogos que se registram no MMCSS.",
            Ops =
            [
                new(Hive.HKLM, MmGames, "GPU Priority", DW, 8, 8),
                new(Hive.HKLM, MmGames, "Priority", DW, 6, 2),
                new(Hive.HKLM, MmGames, "Scheduling Category", SZ, "High", "Medium"),
                new(Hive.HKLM, MmGames, "SFIO Priority", SZ, "High", "Normal"),
            ],
            Impact = Impact.Baixo,
            Evidence = Evidence.Fraca,
        };

        yield return new RegistryTweak
        {
            Id = "sys.responsiveness",
            Category = CatSistema,
            Title = "Reservar menos CPU para tarefas de fundo",
            Description = "Reduz de 20% para 10% a fatia de CPU que o Windows reserva para processos de baixa prioridade enquanto há multimídia rodando.",
            Warning = "Nunca usamos 0: em alguns PCs isso causa estalos no áudio.",
            Ops = [new(Hive.HKLM, MmPath, "SystemResponsiveness", DW, 10, 20)],
            Impact = Impact.Baixo,
            Evidence = Evidence.Fraca,
        };

        yield return new RegistryTweak
        {
            Id = "sys.powerthrottling",
            Category = CatSistema,
            Title = "Desativar Power Throttling",
            Description = "Impede o Windows de jogar apps em segundo plano para núcleos eficientes/baixo clock. Útil em notebooks e CPUs híbridas (Intel 12ª geração+) quando o jogo perde foco.",
            Warning = "Aumenta o consumo de bateria em notebooks.",
            Ops = [new(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", DW, 1)],
            Impact = Impact.Situacional,
            Evidence = Evidence.Moderada,
            Risk = Risk.Moderado,
            RequiresReboot = true,
        };

        yield return new PowerSettingTweak
        {
            Id = "sys.usbsuspend",
            Category = CatSistema,
            Title = "Não desligar portas USB para economizar energia",
            Description = "Desativa a “suspensão seletiva de USB” do plano de energia ativo (na tomada). Evita que mouse, teclado, headset ou controle “durmam” e demorem a responder, ou desconectem no meio da partida.",
            Warning = "Vale para o plano de energia que está ativo agora. Se você trocar de plano, aplique de novo.",
            Subgroup = UsbSub,
            Setting = UsbSuspend,
            Target = 0,
            WindowsDefault = 1,
            Impact = Impact.Situacional,
            Evidence = Evidence.Moderada,
            Availability = () => PowerSettingTweak.IsAvailableHere(UsbSub, UsbSuspend),
        };

        yield return new RegistryTweak
        {
            Id = "sys.visualfx",
            Category = CatSistema,
            Title = "Interface do Windows mais leve",
            Description = "Desliga animações de janelas, da barra de tarefas e a transparência. Deixa o desktop mais ágil em PCs modestos; não altera o FPS dentro dos jogos.",
            Ops =
            [
                new(Hive.HKCU, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", SZ, "0", "1"),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", DW, 0, 1),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", DW, 0, 1),
            ],
            Impact = Impact.Baixo,
            Evidence = Evidence.Forte,
            RequiresReboot = false,
        };

        // ================= GRÁFICOS =================
        yield return new TokenTweak
        {
            Id = "gfx.windowed",
            Category = CatGraficos,
            Title = "Otimizações para jogos em janela",
            Description = "Recurso oficial do Windows 11: jogos DirectX 10/11 em janela sem bordas passam a usar o modelo de apresentação “flip”, com latência próxima da tela cheia.",
            Hive = Hive.HKCU,
            Path = @"Software\Microsoft\DirectX\UserGpuPreferences",
            ValueName = "DirectXUserGlobalSettings",
            TokenKey = "SwapEffectUpgradeEnable",
            TokenValue = "1",
            Impact = Impact.Medio,
            Evidence = Evidence.Forte,
            Recommended = true,
            Availability = () => IsWindows11,
        };

        yield return new TokenTweak
        {
            Id = "gfx.vrr",
            Category = CatGraficos,
            Title = "Taxa de atualização variável em jogos antigos",
            Description = "Opção oficial do Windows (Configurações › Vídeo › Gráficos): deixa jogos DirectX 11 que não suportam G-SYNC/FreeSync sozinhos usarem a taxa variável do monitor. Menos tearing e travadas quando o FPS fica abaixo da taxa do monitor.",
            Warning = "Só tem efeito com monitor G-SYNC/FreeSync com a função ligada no painel da GPU.",
            Hive = Hive.HKCU,
            Path = @"Software\Microsoft\DirectX\UserGpuPreferences",
            ValueName = "DirectXUserGlobalSettings",
            TokenKey = "VRROptimizeEnable",
            TokenValue = "1",
            Impact = Impact.Situacional,
            Evidence = Evidence.Moderada,
        };

        yield return new RegistryTweak
        {
            Id = "gfx.gamedvr",
            Category = CatGraficos,
            Title = "Desligar gravação contínua do Xbox Game Bar",
            Description = "A gravação em segundo plano (“gravar o que aconteceu”) usa o codificador da GPU o tempo todo. A Game Bar continua funcionando para capturas manuais.",
            Ops =
            [
                new(Hive.HKCU, @"System\GameConfigStore", "GameDVR_Enabled", DW, 0, 1),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", DW, 0, 1),
            ],
            Impact = Impact.Medio,
            Evidence = Evidence.Moderada,
            Recommended = true,
        };

        yield return new RegistryTweak
        {
            Id = "gfx.hags",
            Category = CatGraficos,
            Title = "Agendamento de GPU acelerado por hardware (HAGS)",
            Description = "A própria GPU gerencia sua fila de trabalho. Necessário para o DLSS Frame Generation. Ganho típico pequeno (1–3%).",
            Warning = "Em algumas combinações de driver causa micro-travadas. Se notar, desligue aqui mesmo.",
            Ops = [new(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", DW, 2, 1)],
            Impact = Impact.Situacional,
            Evidence = Evidence.Moderada,
            RequiresReboot = true,
        };

        yield return new RegistryTweak
        {
            Id = "gfx.mpo",
            Category = CatGraficos,
            Title = "Desativar Multiplane Overlay (MPO)",
            Description = "Solução documentada pela NVIDIA para tremulação, tela piscando e travadas em janela sem bordas / múltiplos monitores.",
            Warning = "Só use se você tem esses sintomas. Sem eles, o MPO ajuda a economizar recursos.",
            Ops = [new(Hive.HKLM, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", DW, 5)],
            Impact = Impact.Situacional,
            Evidence = Evidence.Moderada,
            Risk = Risk.Moderado,
            RequiresReboot = true,
        };

        // ================= ENTRADA =================
        yield return new RegistryTweak
        {
            Id = "in.mouseaccel",
            Category = CatEntrada,
            Title = "Desligar aceleração do mouse",
            Description = "Desativa “Aumentar precisão do ponteiro”: o mesmo movimento da mão vira sempre a mesma distância na tela. Essencial para memória muscular em jogos sem Raw Input e nos menus.",
            Ops =
            [
                new(Hive.HKCU, @"Control Panel\Mouse", "MouseSpeed", SZ, "0", "1"),
                new(Hive.HKCU, @"Control Panel\Mouse", "MouseThreshold1", SZ, "0", "6"),
                new(Hive.HKCU, @"Control Panel\Mouse", "MouseThreshold2", SZ, "0", "10"),
            ],
            AfterChange = ApplyMouseNow,
            Impact = Impact.Alto,
            Evidence = Evidence.Forte,
            Recommended = true,
        };

        yield return new RegistryTweak
        {
            Id = "in.stickykeys",
            Category = CatEntrada,
            Title = "Bloquear atalhos de acessibilidade no meio do jogo",
            Description = "Impede que apertar Shift 5 vezes (ou segurar Shift/Num Lock) abra a janela de Teclas de Aderência/Filtro e tire você da partida. Os recursos continuam disponíveis nas Configurações.",
            Ops =
            [
                new(Hive.HKCU, @"Control Panel\Accessibility\StickyKeys", "Flags", SZ, "506", "510"),
                new(Hive.HKCU, @"Control Panel\Accessibility\Keyboard Response", "Flags", SZ, "122", "126"),
                new(Hive.HKCU, @"Control Panel\Accessibility\ToggleKeys", "Flags", SZ, "58", "62"),
            ],
            Impact = Impact.Baixo,
            Evidence = Evidence.Forte,
            Recommended = true,
        };

        yield return new RegistryTweak
        {
            Id = "in.gamebarbutton",
            Category = CatEntrada,
            Title = "Botão Xbox do controle não abre a Game Bar",
            Description = "Impede que um toque sem querer no botão central do controle abra a Xbox Game Bar por cima do jogo. A Game Bar continua abrindo com Win + G.",
            Ops = [new(Hive.HKCU, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", DW, 0, 1)],
            Impact = Impact.Baixo,
            Evidence = Evidence.Forte,
        };

        yield return new RegistryTweak
        {
            Id = "in.keyrepeat",
            Category = CatEntrada,
            Title = "Repetição de teclas mais rápida",
            Description = "Reduz o atraso antes de uma tecla segurada começar a repetir. Afeta digitação e menus, não o movimento dentro dos jogos.",
            Ops =
            [
                new(Hive.HKCU, @"Control Panel\Keyboard", "KeyboardDelay", SZ, "0", "1"),
                new(Hive.HKCU, @"Control Panel\Keyboard", "KeyboardSpeed", SZ, "31", "31"),
            ],
            AfterChange = ApplyKeyboardNow,
            Impact = Impact.Baixo,
            Evidence = Evidence.Forte,
        };

        // ================= SEGUNDO PLANO =================
        yield return new ServiceTweak
        {
            Id = "bg.telemetry",
            Category = CatSegundoPlano,
            Title = "Desativar serviço de telemetria (DiagTrack)",
            Description = "O serviço “Experiências do Usuário Conectado e Telemetria” coleta e envia dados de uso em segundo plano, usando CPU e disco. Desligá-lo não afeta Windows Update, Store ou Game Pass.",
            ServiceName = "DiagTrack",
            Impact = Impact.Baixo,
            Evidence = Evidence.Moderada,
            Recommended = true,
            Availability = () => Reg.Get(Hive.HKLM, @"SYSTEM\CurrentControlSet\Services\DiagTrack", "Start") != null,
        };

        yield return new RegistryTweak
        {
            Id = "bg.suggestions",
            Category = CatSegundoPlano,
            Title = "Sem anúncios, sugestões e apps instalados sozinhos",
            Description = "Desliga o ID de publicidade, as “dicas” no Iniciar/Configurações e a instalação silenciosa de apps sugeridos pela Microsoft.",
            Ops =
            [
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", DW, 0, 1),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", DW, 0, 1),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", DW, 0, 1),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", DW, 0, 1),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", DW, 0, 1),
                new(Hive.HKCU, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-353694Enabled", DW, 0, 1),
            ],
            Impact = Impact.Baixo,
            Evidence = Evidence.Forte,
            Recommended = true,
        };

        yield return new RegistryTweak
        {
            Id = "bg.widgets",
            Category = CatSegundoPlano,
            Title = "Desativar Widgets (Notícias e interesses)",
            Description = "Os Widgets mantêm processos WebView rodando e consumindo RAM o tempo todo. Desativa via política oficial; reverter traz tudo de volta.",
            Ops = [new(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", DW, 0)],
            Impact = Impact.Baixo,
            Evidence = Evidence.Moderada,
            Availability = () => IsWindows11,
        };

        yield return new RegistryTweak
        {
            Id = "bg.edge",
            Category = CatSegundoPlano,
            Title = "Edge não fica aberto escondido",
            Description = "Desliga o “Aumento de inicialização” e a execução em segundo plano do Microsoft Edge, que mantêm processos do navegador na memória mesmo com ele fechado. Usa as políticas oficiais do Edge.",
            Warning = "Por ser uma política, o Edge passa a mostrar “Gerenciado pela sua organização” nas configurações. O Edge continua funcionando; só abre um pouco mais devagar.",
            Ops =
            [
                new(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", DW, 0),
                new(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", DW, 0),
            ],
            CleanupKeys = [(Hive.HKLM, @"SOFTWARE\Policies\Microsoft\Edge")],
            Impact = Impact.Baixo,
            Evidence = Evidence.Forte,
            Availability = () => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe")),
        };

        // ================= REDE =================
        yield return new RegistryTweak
        {
            Id = "net.throttling",
            Category = CatRede,
            Title = "Desligar limitação de rede para multimídia",
            Description = "Remove o limite de processamento de pacotes que o Windows aplica quando há áudio/vídeo tocando. Relatos de melhora em 1% low existem, mas não há testes controlados.",
            Ops = [new(Hive.HKLM, MmPath, "NetworkThrottlingIndex", DW, unchecked((int)0xFFFFFFFF), 10)],
            Impact = Impact.Baixo,
            Evidence = Evidence.Fraca,
        };

        // ================= AVANÇADO =================
        yield return new RegistryTweak
        {
            Id = "adv.vbs",
            Category = CatAvancado,
            Title = "Desativar VBS e Integridade de Memória",
            Description = "A virtualização de segurança custa até ~10% de FPS em jogos limitados pela CPU (Tom's Hardware), com 1% low ainda mais afetados.",
            Warning = "A FACEIT EXIGE VBS: CS2 na FACEIT deixa de funcionar. O modo On-Demand do Vanguard (Valorant) também exige VBS/HVCI. Também reduz a proteção do Windows contra malware de kernel. Se o VBS estiver travado pela UEFI, este ajuste não terá efeito.",
            Ops =
            [
                new(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", DW, 0),
                new(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", DW, 0, 1),
            ],
            Impact = Impact.Alto,
            Evidence = Evidence.Forte,
            Risk = Risk.Avancado,
            RequiresReboot = true,
        };

        yield return new PowerPlanTweak
        {
            Id = "adv.power.ultimate",
            Category = CatAvancado,
            Title = "Plano Desempenho Máximo (Ultimate)",
            Description = "Variação do Alto Desempenho que também desliga estados de economia mais profundos. Diferença normalmente mínima para o Alto Desempenho.",
            Warning = "Mais calor e consumo o tempo todo. Não use em notebook.",
            BaseGuid = Power.Ultimate,
            ExtraKey = "power.ultimate",
            Impact = Impact.Baixo,
            Evidence = Evidence.Fraca,
            Risk = Risk.Avancado,
        };

        yield return new RegistryTweak
        {
            Id = "adv.priosep",
            Category = CatAvancado,
            Title = "Separação de prioridade Win32 (0x26)",
            Description = "Ajusta como o Windows divide o tempo de CPU entre o app em primeiro plano e o resto (quantum curto, variável, com boost). Valor popular em guias competitivos.",
            Warning = "Não há benchmark controlado mostrando ganho. Diferenças, se existirem, são pequenas e variam por PC.",
            Ops = [new(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", DW, 0x26, 2)],
            Impact = Impact.Baixo,
            Evidence = Evidence.Fraca,
            Risk = Risk.Avancado,
        };

        yield return new RegistryTweak
        {
            Id = "adv.globaltimer",
            Category = CatAvancado,
            Title = "Resolução de timer global",
            Description = "No Windows 11, um pedido de timer de alta resolução vale só para o processo que pediu. Este ajuste restaura o comportamento global antigo.",
            Warning = "Só faz diferença se algum programa pedir resolução alta. Aumenta o consumo de energia. Efeito em FPS é controverso.",
            Ops = [new(Hive.HKLM, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", DW, 1)],
            Impact = Impact.Baixo,
            Evidence = Evidence.Fraca,
            Risk = Risk.Avancado,
            RequiresReboot = true,
            Availability = () => IsWindows11,
        };
    }

    private static void ApplyMouseNow()
    {
        int Read(string n, int def) => int.TryParse(Reg.GetString(Hive.HKCU, @"Control Panel\Mouse", n), out var v) ? v : def;
        var p = new[] { Read("MouseThreshold1", 6), Read("MouseThreshold2", 10), Read("MouseSpeed", 1) };
        Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, p, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
    }

    private static void ApplyKeyboardNow()
    {
        uint delay = uint.TryParse(Reg.GetString(Hive.HKCU, @"Control Panel\Keyboard", "KeyboardDelay"), out var d) ? d : 1;
        uint speed = uint.TryParse(Reg.GetString(Hive.HKCU, @"Control Panel\Keyboard", "KeyboardSpeed"), out var s) ? s : 31;
        Native.SystemParametersInfo(Native.SPI_SETKEYBOARDDELAY, delay, IntPtr.Zero, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
        Native.SystemParametersInfo(Native.SPI_SETKEYBOARDSPEED, speed, IntPtr.Zero, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
    }
}
