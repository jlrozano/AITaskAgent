# Arranca el backend de CodebaseAnalysisWeb en 5180 con los roles de análisis sobre gpt-5.4 con razonamiento alto.
# gpt-5.4 solo admite temperature=1 (el defecto); con herramientas acepta reasoning_effort=high (sonda del 2026-10-05).
# Se queda sobre gpt-6-sol: Enricher, ReportVerifier y los roles de chat. No se edita ningún appsettings: todo va por línea de comandos.
$S = "C:\Users\juan.rozano\AppData\Local\Temp\claude\C--GeminiCli-AITask-AITaskAgent\49abf624-c44b-4a8e-ae69-11aa00c1e675\scratchpad"
$roles = "FunctionalAuditor","CodeSmellsAuditor","DeadCodeAuditor","FlowsAuditor","SecurityBugAuditor","ArchitectureAuditor","ApiSecurityAuditor","TechnicalDebtAuditor","DiAuditor","ExecutiveSynthesizer"

$old = Get-NetTCPConnection -State Listen -LocalPort 5180 -ErrorAction SilentlyContinue
if ($old) { Stop-Process -Id $old.OwningProcess -Force; Start-Sleep -Seconds 2 }

$a = @("$S\out-web\CodebaseAnalysisWeb.dll", "--urls", "http://127.0.0.1:5180",
       "--contentRoot", "C:\GeminiCli\AITask\AITaskAgent\Samples\CodebaseAnalysisWeb")
foreach ($r in $roles) {
    $p = "--AITaskAgent:LlmProviders:Roles:$r"
    $a += "$p`:Model=gpt-5.4"
    $a += "$p`:Inference:Temperature=1"
    $a += "$p`:Inference:Options:reasoning_effort=high"
}
# Con razonamiento alto una petición puede tardar más de los 300 s por intento del proveedor.
$a += "--AITaskAgent:LlmProviders:Providers:AzureOpenAI:Inference:RequestTimeoutSeconds=900"
$env:ASPNETCORE_ENVIRONMENT = "Development"
Start-Process -FilePath dotnet -ArgumentList $a -WindowStyle Hidden `
    -RedirectStandardOutput "$S\web-5180.out.log" -RedirectStandardError "$S\web-5180.err.log"
