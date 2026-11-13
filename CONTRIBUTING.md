# Contribuindo com o Hertzo

Obrigado pelo interesse! O Hertzo tem uma regra que vale mais que qualquer recurso novo: **honestidade com quem usa**.
Antes de abrir um PR, leia os princípios abaixo.

## Princípios

1. **Todo ajuste é reversível.** Antes de escrever, o valor original vai para o diário (`Journal`). Reverter devolve o valor exato, ou apaga o valor se ele não existia.
2. **Evidência declarada.** Cada ajuste tem rótulos de impacto, risco e evidência (`Forte`, `Moderada`, `Fraca`). Ajustes com evidência fraca nunca entram no “Otimizar agora”. Se não há teste controlado, o rótulo é `Fraca`, mesmo que o ajuste seja popular.
3. **Nunca:** desativar antivírus, Firewall ou Windows Update; mexer em anti-cheat ou arquivos de jogos; injetar código em processos; usar `bcdedit`; desligar mitigações de CPU; remover apps do sistema; desligar o arquivo de paginação.
4. **Nada de downloads em tempo de execução.** Ferramentas de terceiros são embutidas, com licença e hash conferido.
5. **Admin é responsabilidade.** Qualquer dado lido de um lugar que um processo sem admin consegue escrever é tratado como não confiável (veja `Core/Security.cs`).

## Propondo um ajuste novo

Abra uma issue com o modelo **Novo ajuste** contendo:
- o que muda (chave de registro, serviço, configuração de energia…) e o valor padrão do Windows;
- a fonte (documentação da Microsoft, do fabricante, benchmark controlado);
- o impacto esperado e para quem ele *não* serve;
- como reverter.

Ajustes entram em `Hertzo/Core/TweakCatalog.cs`. Normalmente não é preciso código novo: `RegistryTweak`, `TokenTweak`, `ServiceTweak`, `PowerPlanTweak` e `PowerSettingTweak` cobrem quase tudo.
Se o ajuste escreve num lugar novo do registro, `Tweak.Touches` precisa reconhecê-lo, ou o diário vai recusar o backup. Isso é proposital.

## Ambiente

- Windows 10 21H2+ ou Windows 11, x64
- .NET 10 SDK
- Terminal **como administrador** para rodar (`dotnet run --project Hertzo`)

```powershell
dotnet build Hertzo\Hertzo.csproj
.\build.ps1          # gera dist\Hertzo.exe (arquivo único, autocontido)
```

## Estilo

- C# moderno, `Nullable` ligado, sem dependências além das já usadas (Vortice e System.Management).
- Textos da interface em português do Brasil, diretos e sem exagero (“pode ajudar”, não “+50% de FPS”).
- Identidade visual: veja a seção “Identidade visual” do README. Uma cor de sinal só (`#FF5B1F`), cantos retos, sem gradientes nem neon.

## Pull requests

- Um assunto por PR.
- Descreva **como você testou**, de preferência com uma medição antes e depois na tela Medição quando o assunto for desempenho.
- Mudanças em `Security.cs`, `Journal.cs` ou na Limpeza precisam mostrar o caso de ataque sendo bloqueado.
