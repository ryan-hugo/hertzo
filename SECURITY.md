# Política de segurança

O Hertzo roda como **administrador** e altera configurações do Windows. Por isso, segurança é parte do projeto, não um detalhe.

## Como reportar uma vulnerabilidade

**Não abra uma issue pública.** Use o reporte privado do GitHub:

1. Abra a aba **Security** deste repositório.
2. Clique em **Report a vulnerability**.
3. Descreva o problema, a versão do Hertzo, o Windows usado e, se possível, os passos para reproduzir.

A resposta inicial vem em até 7 dias. Correções de segurança têm prioridade sobre qualquer outra mudança e saem em uma versão própria, com crédito para quem reportou (se quiser).

## O que interessa especialmente

- Qualquer forma de um processo **sem admin** fazer o Hertzo (com admin) escrever no registro, apagar ou executar algo que não deveria.
- Contornar a validação do diário de backup (`%ProgramData%\Hertzo\journal.json`).
- Fazer a Limpeza apagar arquivos fora das pastas alvo.
- Trocar o PresentMon extraído por outro executável.
- Ajustes que, ao contrário do que o Hertzo afirma, não são reversíveis.

## Garantias que o Hertzo procura manter

As proteções atuais estão descritas no README, na seção [Segurança](README.md#segurança), e implementadas em
[`Hertzo/Core/Security.cs`](Hertzo/Core/Security.cs). Mudanças nessas áreas precisam de testes que mostrem o ataque sendo bloqueado.

## Versões com suporte

Só a versão mais recente recebe correções.
