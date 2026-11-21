# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/), com versões em [SemVer](https://semver.org/lang/pt-BR/).

## [1.1.0] — 2026-11-21

### Novo
- **Segundo plano e bandeja:** o Hertzo ganha um ícone perto do relógio, com abrir, salvar o replay, ligar/desligar o replay e sair. Fechar a janela deixa o Hertzo na bandeja (opcional), e o replay e os atalhos continuam valendo.
- **Abrir com o Windows:** abre sozinho, só na bandeja, ao entrar no Windows. Usa uma tarefa agendada com privilégios elevados e uma cópia em `Arquivos de Programas\Hertzo`, onde programas sem admin não conseguem trocar o executável. Também dá para usar `Hertzo.exe --autostart-on` / `--autostart-off`.
- **Replay sempre ligado:** liga o replay toda vez que o Hertzo abre, mesmo que tenha sido desligado antes.
- Tudo isso na nova seção **Clipes › 08 · Sempre**.

### Corrigido
- **Configurações que “sumiam”:**
  - os arquivos de configuração agora são gravados de forma atômica e com cópia de segurança (`.bak`);
  - se um arquivo estiver ilegível, o Hertzo usa a cópia em vez de voltar aos padrões em silêncio;
  - abrir o Hertzo de novo traz a janela já aberta, em vez de criar uma segunda instância que disputava atalhos e sobrescrevia as configurações.
- **Migração do Pulso:** enquanto o Hertzo ainda não tiver gravado um arquivo, mudanças feitas depois no Pulso (backups, clipes, configurações) são trazidas de novo.

### Segurança
- O executável único extraía as DLLs nativas do WPF para `%TEMP%\.net`, onde qualquer programa sem admin pode trocá-las, e elas eram carregadas com privilégios de administrador. Agora o Hertzo se reabre com a extração apontando para `%ProgramData%\Hertzo\runtime`, só de administradores. Isso era pré-requisito para o início automático, que roda como admin sem perguntar.

## [1.0.0] — 2026-11-13

Primeira versão pública.

### Otimizar
- **Check-up** com peso real: monitor abaixo da taxa máxima (corrige com reversão automática em 15 s), RAM em canal único, XMP/EXPO provavelmente desligado, plano de energia, Modo de Jogo, gravação do Game Bar, aceleração do mouse, driver de vídeo antigo, espaço em disco e VBS.
- **25 ajustes** em 6 categorias, cada um com selos de impacto, risco e evidência. Os avançados ficam escondidos e pedem confirmação.
- **Jogos:** detecção de Steam, Epic, Riot, Battle.net e EA. Por jogo: GPU de alto desempenho, desativar otimizações de tela cheia e prioridade Alta.

### Medir
- Medição de FPS com o PresentMon (Intel): FPS médio, 1% low, 0,1% low, P99 e engasgos.
- Comparação antes × depois que separa diferença real de variação normal.

### Capturar
- Replay instantâneo na RAM (5 s a 10 min) e gravação manual.
- Captura pela Windows.Graphics.Capture e H.264 por hardware (AMF, NVENC, QuickSync).
- Áudio do jogo e do microfone, com volumes ajustáveis ao vivo e faixa extra só do microfone.
- Atalhos configuráveis e aviso na tela do jogo.

### Manter
- Limpeza de temporários, relatórios de erro, caches de shader, despejos de memória e Lixeira.
- Programas da inicialização.

### Proteger
- Backup por valor, ponto de restauração e **Desfazer tudo**.
- `%ProgramData%\Hertzo` restrita a administradores.
- Diário de backup validado: só aceita restaurar valores que o próprio ajuste altera.
- Limpeza resistente a junções.
- Sites e arquivos abertos sem privilégios de administrador.

### Migração
- Para quem testou a versão interna “Pulso”: diário, clipes, medições e configurações são copiados na primeira abertura. As pastas antigas ficam intactas.
