namespace TextToTemplateDemo;

/// <summary>
/// Informe de ejemplo que produce el "auditor": markdown de varias líneas con todo lo que podría
/// romper una plantilla - tildes y eñes, <c>ruta:línea</c>, un bloque de código con <c>&lt;</c>,
/// <c>&gt;</c> y llaves, llaves dobles que parecen un placeholder y una referencia <c>@[...]</c>.
/// </summary>
internal static class SampleReport
{
    /// <summary>
    /// Saltos de línea normalizados a <c>\n</c> para que la comparación no dependa de cómo se
    /// guardó este fichero fuente.
    /// </summary>
    public static string Text { get; } = """
        # Auditoría del informe `modulo-pagos.md`

        ## Hallazgos

        1. **Afirmación sin evidencia**: «el servicio reintenta tres veces» — no aparece en
           `src/Pagos/PagoService.cs:142`; allí solo hay un `catch` que registra y relanza.
        2. La cardinalidad `Cliente 1..* Cuenta` sí está respaldada por
           `db/schema/cuentas.sql:17` (`FK_CUENTA_CLIENTE`).
        3. Símbolos raros: ñ, ü, ç, €, ¿?, ¡!, «», 'comillas', "dobles", `$1`, `$&`, `\n` literal.

        ## Fragmento citado

        ```csharp
        public async Task<Result<Pago>> ProcesarAsync(Dictionary<string, List<int>> lotes)
        {
            if (lotes.Count > 0 && lotes.Count < 10) { return await _repo.GuardarAsync(lotes); }
            var plantilla = $"{{ input.nombre }} -> {lotes}";
            return Result<Pago>.Fail("sin <datos>");
        }
        ```

        ## Placeholders que no deberían resolverse

        - Doble llave literal: {{ context.informe_auditoria }} y {{ value }}.
        - Comentario Scriban literal: {{# esto no es un comentario de la plantilla #}}.
        - HTML: <div class="x">a &amp; b</div>

        Veredicto: **parcialmente respaldado** (2 de 3).
        """.ReplaceLineEndings("\n");

    /// <summary>
    /// Mismo informe con referencias <c>@[ruta]</c>, la sintaxis legacy de inclusión de ficheros
    /// de <c>TinyTemplateRendered</c>. Sirve para ver si esa pasada también actúa sobre el texto
    /// ya interpolado.
    /// </summary>
    public static string TextWithIncludeLikeRefs { get; } = (Text + """

        ## Referencias con arroba

        - Documento que no existe: @[docs/arquitectura.md]
        - Fichero que sí existe junto a las plantillas: @[verificador_system.md]
        - Fuera de la carpeta de plantillas: @[../TextToTemplateDemo.dll]
        """).ReplaceLineEndings("\n");
}
