using System.ComponentModel;
using System.Diagnostics;

namespace CodebaseAnalysis.Core.Support;

/// <summary>
/// <see cref="Process.WaitForExitAsync(CancellationToken)"/> deja de esperar al cancelar, pero no
/// termina el proceso: el indexador o el enriquecedor seguían vivos (y escribiendo en la BD) después de
/// cancelar el análisis.
/// </summary>
public static class ProcessCancellation
{
    /// <summary>
    /// Espera a que el proceso termine; si se cancela, lo mata con todo su árbol y relanza la
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task WaitForExitOrKillAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Terminó entre la comprobación y el Kill, o ya no se puede acceder a él.
            }

            throw;
        }
    }
}
