# GameTranslate — Legendas Amarelas em Tempo Real para Jogos (EN → PT-BR)

Camada transparente para Windows que lê legendas em inglês diretamente da tela do jogo, traduz em tempo real para **Português do Brasil (PT-BR)** e exibe por cima com o visual clássico de **legendas amarelas com contorno preto** (estilo cinema/séries).

Distribuído como um **único arquivo `GameTranslate.exe` self-contained** — não exige instalação de .NET, Python, Tesseract nem nenhum componente externo em Windows 10 (2004+) ou Windows 11.

## Recursos Principais

- **Legenda Amarela Estilo Séries/Cinema**: Renderização vetorial nítida (`OutlinedTextBlock`) com contorno preto arredondado, sombra suave e fundo escuro opcional para cobrir a legenda original em inglês.
- **Sobreposição Inteligente (`WDA_EXCLUDEFROMCAPTURE`)**: A camada amarela pode ficar **exatamente em cima** da legenda em inglês sem que o OCR leia a própria tradução — a captura enxerga direto o jogo por baixo!
- **100% Seguro para Anti-Cheat**: Não injeta DLL no processo do jogo nem altera arquivos do jogo; usa apenas captura de tela nativa do Windows e janela transparente `click-through`.
- **OCR Nativo do Windows (`Windows.Media.Ocr`)**: Rápido (~15–40ms), leve e já embutido no Windows 10/11 — funciona mesmo se o PC só tiver o idioma `pt-BR` instalado.
- **Baixo uso de CPU**: Comparação ultrarrápida de frames (`fingerprint` 64×16) pula o OCR quando a legenda não mudou + cache LRU de traduções (0 ms para falas repetidas) + filtro anti-tremulação (Levenshtein).
- **Tradução Pronta para Uso**: Funciona imediatamente sem chave de API (Google Tradutor com fallback automático para MyMemory), além de suporte opcional a **DeepL API** e **OpenAI / Ollama local**.
- **Atalhos Globais + Bandeja do Sistema (System Tray)**: Controle tudo sem sair do jogo.

## Atalhos Globais

| Atalho | Ação |
| --- | --- |
| `Alt + T` | Iniciar ou pausar a tradução em tempo real |
| `Alt + H` | Mostrar ou ocultar a legenda amarela |
| `Alt + R` | Selecionar novamente a área das legendas do jogo |
| `Alt + O` | Forçar leitura e tradução instantânea |

## Como Usar

1. Execute `GameTranslate.exe`.
2. Abra seu jogo em modo **Janela Sem Bordas (Borderless Fullscreen)** ou **Janela**.
3. Clique em **🎯 Selecionar Área (Alt+R)** e arraste um retângulo sobre o local onde aparecem as legendas em inglês do jogo.
4. Clique em **▶ Iniciar Tradução (Alt+T)** e minimize para a bandeja (System Tray).
5. Opcional: ajuste o tamanho da fonte, cor amarela, contorno preto e opacidade do fundo escuro na aba **Estilo da Legenda**.

> **Dica para jogos 3D com fundo muito claro/colorido:** na aba **Captura e OCR**, mude o filtro para **Texto Branco/Amarelo** para isolar as letras da legenda e ignorar o cenário em movimento.

## Como Compilar o `.exe` Único

Requer apenas o .NET 6 SDK (ou superior) na máquina de desenvolvimento:

```powershell
.\build-single-exe.ps1
```

O executável único e portátil será gerado em `dist\GameTranslate.exe`.

## Stack de Testes (xUnit)

O projeto possui uma suíte automatizada em `tests\GameTranslate.Tests` cobrindo reparo fonotático de fontes pixeladas (`R → A`), pré-processamento de imagem, fingerprint anti-fundo animado, similaridade anti-tremulação, formatação compacta de menus, configurações e integração real com Windows OCR + Tradutor Híbrido.

```powershell
# Rodar toda a suíte (unitários + integração)
dotnet test tests\GameTranslate.Tests\GameTranslate.Tests.csproj -c Release

# Rodar apenas testes unitários offline (ultra-rápido)
dotnet test tests\GameTranslate.Tests\GameTranslate.Tests.csproj -c Release --filter "Category!=Integration"
```

Ou simplesmente execute `run-tests.bat`.
