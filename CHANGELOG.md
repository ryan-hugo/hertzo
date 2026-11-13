# Changelog

Formato baseado em [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/), com versões em [SemVer](https://semver.org/lang/pt-BR/).

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
