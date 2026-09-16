<!--
Descreva o PROBLEMA antes da solução: o diff mostra o que mudou, o descritivo
precisa explicar por que a mudança é essa e não outra. Se o PR fecha uma issue,
use "Closes #N" — em inglês, fora de crase — para o fechamento automático.
-->

## O problema

<!-- O que estava errado ou faltando, e como se manifestava. Para bug de
comportamento, o que o código produzia antes — de preferência medido, não
descrito. -->

## A mudança

<!-- O que foi feito, e as decisões que não são óbvias pelo diff: por que esta
abordagem, o que foi descartado e por quê. -->

## Verificação

<!-- Como você sabe que funciona. Cole a saída, não a conclusão. -->

## Checklist

- [ ] `dotnet build PowerLinq.slnx -c Release -warnaserror` sem avisos
- [ ] `dotnet test` verde
- [ ] Se corrige comportamento: **existe teste que reprovaria antes da correção**
- [ ] Se toca caminho quente de tradução ou materialização: benchmark antes/depois no descritivo
- [ ] Se acrescenta mensagem de erro: as duas chaves nos `.resx` (`Messages.resx` e `Messages.pt-BR.resx`)
- [ ] Se muda comportamento documentado: README atualizado — incluindo números de performance que a mudança invalida
- [ ] Se muda API pública: `CHANGELOG.md` atualizado

<!--
Sobre o terceiro item: um teste escrito depois da correção pode passar pelo
motivo errado. Se não der para escrever o teste antes, quebre a correção de
propósito e confirme que o teste reprova.
-->
