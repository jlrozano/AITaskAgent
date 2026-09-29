/*
 * JavaAnalyzer (analyzerVersion "2", schema 2): analizador Java del enriquecimiento nativo del grafo
 * (docs/plans/2026-09-28-enriquecimiento-nativo-java-plan.md, T2; registros field y ref en
 * docs/plans/2026-09-29-referencias-tipos-campos-plan.md, Q1-Q4). Un solo fichero, Java 17, sin dependencias.
 *
 * Uso (source launcher, sin paso de compilacion):
 *   java <JvmOptions> --add-exports jdk.compiler/com.sun.tools.javac.code=ALL-UNNAMED
 *        [--add-exports jdk.compiler/com.sun.tools.javac.api=ALL-UNNAMED] JavaAnalyzer.java --request <request.txt>
 * El launcher aplica los --add-exports tanto a la compilacion en memoria como a la ejecucion (comprobado con
 * Temurin 17.0.20.1). El export de javac.code es obligatorio (Symbol.ClassSymbol y Flags). El de javac.api es
 * opcional y solo sirve en lotes (batchSize > 0): permite atribuir unicamente los ficheros del lote y no los que
 * javac carga por -sourcepath; sin el el analisis funciona igual pero cada lote tarda mas.
 *
 * Peticion (UTF-8, clave=valor, listas con la clave repetida, '#' comenta): root=<abs>, out=<carpeta>,
 * file=<rel> (repetida), classpath=<abs> (repetida), defaultEncoding=, batchSize= (0 = un lote),
 * maxParallelBatches= (1), sourceRoot=<abs> (repetida, solo en lotes; si falta se calcula por el package).
 * Si defaultEncoding esta vacia se toma project.build.sourceEncoding del pom.xml de root, y si no, UTF-8.
 * Cada fuente (tambien las que javac carga por -sourcepath en lotes) se lee como UTF-8 si sus bytes son UTF-8 valido,
 * con o sin BOM, y si no con la codificacion por defecto (P6).
 *
 * Resultado: <out>/java-analysis.jsonl, JSON Lines UTF-8 con registros "k" = file | diag | type | method | call | field |
 * ref y un summary final (contrato en el plan, "Contrato del fichero intermedio"). Se escribe en .tmp y se renombra al terminar:
 * si el fichero existe, la pasada termino. Progreso por stdout: "@@progress phase=<parse|analyze|scan> done=<n>
 * total=<n>"; cualquier otra linea es log.
 * Precisiones del contrato: la key de un constructor usa el nombre simple de la clase (a.B#B(int)); en una interfaz
 * las interfaces extendidas van en "interfaces" y "super" es null; "super" solo sale con extends explicito; la linea
 * de una llamada es la del nombre del metodo y la de un new la de la palabra new; un destino dentro de una clase
 * anonima o local lleva el nombre binario (a.B$1Local#go()); un new de clase anonima apunta al constructor del
 * supertipo; el super() implicito que inserta javac no se emite.
 *
 * Schema 2. Registro field por campo o constante de enum de un tipo con nombre: {"k":"field","path","chain","ownerFqn",
 * "name","line","end","typeFqn" (borrado),"access","static","final","enumConstant","doc"}. Registro ref, agregado por
 * fichero y por (from, fromField, fromType, to, kind, access), nunca uno por referencia: {"k":"ref","path","from" (key del
 * metodo o null),"fromType" (tipo con nombre que la contiene),"fromField" (ownerFqn#campo si esta en el tipo declarado o el
 * inicializador de un campo, si no null),"to" (FQN del tipo u ownerFqn#campo),"kind" (type|field),"access" (read|write|
 * readwrite en campos, null en tipos),"contexts" (solo tipos, [] en campos: param, return, field, local, cast, instanceof,
 * generic, static_access, class_literal, annotation, throws, catch, new_implicit, new_array, other),"origin" (siempre
 * project),"count" (referencias agregadas),"lines" (lineas unicas, ordenadas)}.
 * Precisiones: si from y fromField son null la referencia esta en un inicializador static o de instancia, o en las
 * anotaciones o argumentos de tipo del propio tipo. Lo que hay dentro de una clase anonima o local (tambien una lambda) se
 * atribuye al metodo o campo que la contiene, como las llamadas, y un destino declarado en ella lleva el nombre binario
 * (a.B$1Local, a.B$1#campo). La linea de un nombre calificado es la de su ultimo segmento. Escritura = destino de "=";
 * readwrite = destino de op=, ++ y --; el inicializador de la declaracion no cuenta como escritura. No se emiten: los import;
 * los tipos de extends/implements (si sus argumentos de tipo, como generic); el calificador de otro nombre de tipo (a en
 * a.B.C); un tipo referido desde si mismo (se cuenta en selfExcluded); el nombre de un new con constructor escrito, que ya
 * es un call; this, super, length de un array y los arboles que sintetiza javac. new_implicit = new de un tipo del proyecto
 * sin constructor escrito, o de una clase anonima cuyo supertipo es una interfaz o no tiene constructor escrito.
 * static_access = el tipo califica un miembro (X.CONST, X.m(), X::m). new_array = new X[n]. Solo se detallan los destinos
 * project; los demas se cuentan en summary.refs (type y field por origen, contexts de los refs de tipo del proyecto por
 * ocurrencia, records, selfExcluded, unresolvedByReason) junto a summary.fields.
 *
 * Codigo de salida: 0 termino (aunque haya diagnosticos), 1 peticion invalida, 2 javac no pudo arrancar,
 * 3 fallo inesperado durante el analisis (p. ej. OutOfMemoryError). Ojo: si el launcher no puede compilar este fichero
 * (p. ej. falta el --add-exports de javac.code) tambien sale con 1 y escribe "error: compilation failed" en stderr.
 *
 * El fichero es ASCII a proposito (sin tildes en los comentarios, \\uXXXX en los literales): el source launcher lo
 * compila con la codificacion de la plataforma y no admite -encoding.
 */

import com.sun.source.tree.*;
import com.sun.source.util.*;
import com.sun.tools.javac.code.Flags;
import com.sun.tools.javac.code.Kinds;
import com.sun.tools.javac.code.Symbol;

import javax.lang.model.element.*;
import javax.lang.model.type.*;
import javax.lang.model.util.Elements;
import javax.lang.model.util.Types;
import javax.tools.*;
import java.io.*;
import java.lang.management.ManagementFactory;
import java.lang.reflect.InvocationTargetException;
import java.lang.reflect.Method;
import java.net.URI;
import java.nio.ByteBuffer;
import java.nio.charset.*;
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

public class JavaAnalyzer {
    static final String ANALYZER_VERSION = "2";
    static final int SCHEMA = 2;
    static final String RESULT_NAME = "java-analysis.jsonl";
    static final int PROGRESS_EVERY = 500;

    static final String[] ORIGINS = {"project", "jdk", "jar", "unresolved"};
    static final int O_PROJECT = 0, O_JDK = 1, O_JAR = 2, O_UNRESOLVED = 3;
    /** Contextos de un ref de tipo, en el orden en que se escriben; el indice es el bit de la mascara. */
    static final String[] CONTEXTS = {"param", "return", "field", "local", "cast", "instanceof", "generic", "static_access",
            "class_literal", "annotation", "throws", "catch", "new_implicit", "new_array", "other"};
    static final int C_PARAM = 0, C_RETURN = 1, C_FIELD = 2, C_LOCAL = 3, C_CAST = 4, C_INSTANCEOF = 5, C_GENERIC = 6,
            C_STATIC_ACCESS = 7, C_CLASS_LITERAL = 8, C_ANNOTATION = 9, C_THROWS = 10, C_CATCH = 11, C_NEW_IMPLICIT = 12,
            C_NEW_ARRAY = 13, C_OTHER = 14;
    static final String[] ACCESS = {"read", "write", "readwrite"};
    static final int A_READ = 0, A_WRITE = 1, A_READWRITE = 2;

    static int originIndex(String o) {
        switch (o) {
            case "project": return O_PROJECT;
            case "jdk": return O_JDK;
            case "jar": return O_JAR;
            default: return O_UNRESOLVED;
        }
    }

    static PrintStream stdout;
    static volatile long heapPeak;

    public static void main(String[] args) {
        stdout = new PrintStream(new FileOutputStream(FileDescriptor.out), true, StandardCharsets.UTF_8);
        int code;
        try {
            code = new JavaAnalyzer(Request.load(args)).run();
        } catch (RequestException e) {
            log("error: petici\u00f3n inv\u00e1lida: " + e.getMessage());
            code = 1;
        } catch (CompilerStartException e) {
            log("error: javac no pudo arrancar: " + e.getMessage());
            if (e.getCause() != null) e.getCause().printStackTrace();
            code = 2;
        } catch (Throwable t) {
            log("error: fallo durante el an\u00e1lisis: " + t);
            t.printStackTrace();
            code = 3;
        }
        stdout.flush();
        System.exit(code);
    }

    static synchronized void log(String s) { stdout.println(s); }

    static synchronized void progress(String phase, int done, int total) {
        stdout.println("@@progress phase=" + phase + " done=" + done + " total=" + total);
    }

    // ------------------------------------------------------------------ peticion

    static final class RequestException extends Exception { RequestException(String m) { super(m); } }

    static final class CompilerStartException extends Exception {
        CompilerStartException(String m, Throwable c) { super(m, c); }
    }

    static final class Request {
        Path root, out;
        final List<String> files = new ArrayList<>(), classpath = new ArrayList<>(), sourceRoots = new ArrayList<>();
        String defaultEncoding = "";
        int batchSize = 0, maxParallelBatches = 1;

        static Request load(String[] args) throws RequestException {
            Path p = null;
            for (int i = 0; i < args.length; i++) {
                if (args[i].equals("--request") && i + 1 < args.length) p = Paths.get(args[++i]);
                else if (args[i].startsWith("--request=")) p = Paths.get(args[i].substring("--request=".length()));
                else throw new RequestException("argumento desconocido: " + args[i]);
            }
            if (p == null) throw new RequestException("uso: java ... JavaAnalyzer.java --request <request.txt>");
            List<String> lines;
            try {
                lines = Files.readAllLines(p, StandardCharsets.UTF_8);
            } catch (IOException e) {
                throw new RequestException("no se puede leer " + p + ": " + e);
            }
            Request r = new Request();
            String root = null, out = null;
            for (String raw : lines) {
                String l = raw.startsWith("\ufeff") ? raw.substring(1) : raw;
                if (l.isBlank() || l.stripLeading().startsWith("#")) continue;
                int eq = l.indexOf('=');
                if (eq < 0) throw new RequestException("l\u00ednea sin '=': " + l);
                String k = l.substring(0, eq).trim(), v = l.substring(eq + 1).trim();
                switch (k) {
                    case "root": root = v; break;
                    case "out": out = v; break;
                    case "file": if (!v.isEmpty()) r.files.add(v.replace('\\', '/')); break;
                    case "classpath": if (!v.isEmpty()) r.classpath.add(v); break;
                    case "sourceRoot": if (!v.isEmpty()) r.sourceRoots.add(v); break;
                    case "defaultEncoding": r.defaultEncoding = v; break;
                    case "batchSize": r.batchSize = parseInt(k, v, 0); break;
                    case "maxParallelBatches": r.maxParallelBatches = Math.max(1, parseInt(k, v, 1)); break;
                    default: log("aviso: clave desconocida en la petici\u00f3n: " + k);
                }
            }
            if (root == null || root.isEmpty()) throw new RequestException("falta root");
            if (out == null || out.isEmpty()) throw new RequestException("falta out");
            r.root = Paths.get(root).toAbsolutePath().normalize();
            if (!Files.isDirectory(r.root)) throw new RequestException("root no es una carpeta: " + r.root);
            r.out = Paths.get(out).toAbsolutePath().normalize();
            return r;
        }

        static int parseInt(String k, String v, int dflt) throws RequestException {
            if (v.isEmpty()) return dflt;
            try {
                int n = Integer.parseInt(v);
                if (n < 0) throw new NumberFormatException();
                return n;
            } catch (NumberFormatException e) {
                throw new RequestException(k + " no es un entero >= 0: " + v);
            }
        }
    }

    // ------------------------------------------------------------------ codificacion (P6)

    static final Pattern POM_ENCODING = Pattern.compile("<project\\.build\\.sourceEncoding>\\s*([^<\\s]+)\\s*</project\\.build\\.sourceEncoding>");

    static Charset resolveDefaultEncoding(String configured, Path root) throws RequestException {
        String name = configured;
        String from = "petici\u00f3n";
        if (name == null || name.isBlank()) {
            name = null;
            Path pom = root.resolve("pom.xml");
            if (Files.isRegularFile(pom)) {
                try {
                    Matcher m = POM_ENCODING.matcher(new String(Files.readAllBytes(pom), StandardCharsets.ISO_8859_1));
                    if (m.find()) { name = m.group(1); from = "pom.xml"; }
                } catch (IOException e) {
                    log("aviso: no se puede leer " + pom + ": " + e);
                }
            }
            if (name == null) { name = "UTF-8"; from = "por defecto"; }
        }
        try {
            Charset cs = Charset.forName(name);
            log("codificaci\u00f3n por defecto: " + cs.name() + " (" + from + ")");
            return cs;
        } catch (IllegalArgumentException e) {
            throw new RequestException("codificaci\u00f3n desconocida: " + name);
        }
    }

    static final class Decoded {
        final String text, encoding;
        Decoded(String t, String e) { text = t; encoding = e; }
    }

    /** UTF-8 si los bytes son UTF-8 valido (con o sin BOM); si no, la codificacion por defecto. */
    static Decoded decode(byte[] b, Charset dflt) {
        boolean bom = b.length >= 3 && b[0] == (byte) 0xEF && b[1] == (byte) 0xBB && b[2] == (byte) 0xBF;
        int off = bom ? 3 : 0;
        if (bom) return new Decoded(new String(b, off, b.length - off, StandardCharsets.UTF_8), "UTF-8");
        CharsetDecoder d = StandardCharsets.UTF_8.newDecoder()
                .onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT);
        try {
            return new Decoded(d.decode(ByteBuffer.wrap(b)).toString(), "UTF-8");
        } catch (CharacterCodingException e) {
            return new Decoded(new String(b, dflt), dflt.name());
        }
    }

    static final class SrcFile extends SimpleJavaFileObject {
        final String rel, encoding;
        String text;
        SrcFile(URI uri, String rel, String text, String encoding) {
            super(uri, Kind.SOURCE);
            this.rel = rel; this.text = text; this.encoding = encoding;
        }
        @Override public CharSequence getCharContent(boolean ignoreEncodingErrors) { return text; }
    }

    /** Fuente que javac carga por -sourcepath (lotes): se decodifica con la misma regla P6 que los ficheros del lote. */
    static final class DecodingFileObject extends ForwardingJavaFileObject<JavaFileObject> {
        final Charset dflt;
        DecodingFileObject(JavaFileObject f, Charset dflt) { super(f); this.dflt = dflt; }
        JavaFileObject inner() { return fileObject; }
        @Override public CharSequence getCharContent(boolean ignoreEncodingErrors) throws IOException {
            try (InputStream in = fileObject.openInputStream()) { return decode(in.readAllBytes(), dflt).text; }
        }
    }

    static final class DecodingFileManager extends ForwardingJavaFileManager<StandardJavaFileManager> {
        final Charset dflt;
        DecodingFileManager(StandardJavaFileManager m, Charset dflt) { super(m); this.dflt = dflt; }

        static FileObject unwrap(FileObject f) { return f instanceof DecodingFileObject ? ((DecodingFileObject) f).inner() : f; }

        @Override public Iterable<JavaFileObject> list(Location loc, String pkg, Set<JavaFileObject.Kind> kinds, boolean recurse) throws IOException {
            Iterable<JavaFileObject> it = super.list(loc, pkg, kinds, recurse);
            if (loc != StandardLocation.SOURCE_PATH) return it;
            List<JavaFileObject> r = new ArrayList<>();
            for (JavaFileObject f : it) r.add(f.getKind() == JavaFileObject.Kind.SOURCE ? new DecodingFileObject(f, dflt) : f);
            return r;
        }
        @Override public String inferBinaryName(Location loc, JavaFileObject f) {
            return super.inferBinaryName(loc, (JavaFileObject) unwrap(f));
        }
        @Override public boolean isSameFile(FileObject a, FileObject b) { return super.isSameFile(unwrap(a), unwrap(b)); }
    }

    // ------------------------------------------------------------------ JSON

    static final class J {
        final StringBuilder sb = new StringBuilder(256);
        boolean first = true;
        J() { sb.append('{'); }
        J key(String k) { if (!first) sb.append(','); first = false; str(sb, k); sb.append(':'); return this; }
        J s(String k, CharSequence v) { key(k); if (v == null) sb.append("null"); else str(sb, v); return this; }
        J n(String k, long v) { key(k); sb.append(v); return this; }
        J lineNo(String k, long v) { key(k); if (v <= 0) sb.append("null"); else sb.append(v); return this; }
        J b(String k, boolean v) { key(k); sb.append(v); return this; }
        J raw(String k, String json) { key(k); sb.append(json); return this; }
        J arr(String k, Collection<String> v) { key(k); arr(sb, v); return this; }
        String end() { sb.append('}'); return sb.toString(); }

        static void arr(StringBuilder sb, Collection<String> v) {
            if (v == null) { sb.append("null"); return; }
            sb.append('[');
            boolean f = true;
            for (String x : v) { if (!f) sb.append(','); f = false; if (x == null) sb.append("null"); else str(sb, x); }
            sb.append(']');
        }

        static final char[] HEX = "0123456789abcdef".toCharArray();

        static void str(StringBuilder sb, CharSequence s) {
            sb.append('"');
            int n = s.length();
            for (int i = 0; i < n; i++) {
                char c = s.charAt(i);
                switch (c) {
                    case '"': sb.append("\\\""); break;
                    case '\\': sb.append("\\\\"); break;
                    case '\n': sb.append("\\n"); break;
                    case '\r': sb.append("\\r"); break;
                    case '\t': sb.append("\\t"); break;
                    case '\b': sb.append("\\b"); break;
                    case '\f': sb.append("\\f"); break;
                    default:
                        if (c < 0x20) {
                            sb.append("\\u00").append(HEX[c >> 4]).append(HEX[c & 15]);
                        } else if (Character.isHighSurrogate(c)) {
                            if (i + 1 < n && Character.isLowSurrogate(s.charAt(i + 1))) sb.append(c).append(s.charAt(++i));
                            else sb.append('\ufffd'); // sustituto suelto: no es UTF-16 valido
                        } else if (Character.isLowSurrogate(c)) {
                            sb.append('\ufffd');
                        } else {
                            sb.append(c);
                        }
                }
            }
            sb.append('"');
        }
    }

    // ------------------------------------------------------------------ estadisticas

    static final class Stats {
        long parseMs, analyzeMs, scanMs;
        int files, errors, warnings;
        final Map<String, Long> calls = new LinkedHashMap<>();
        final TreeMap<String, Long> unresolvedByReason = new TreeMap<>();
        final TreeSet<String> missingPackages = new TreeSet<>();
        long originFallback;
        /** Referencias por origen (indice ORIGINS), de tipo y de campo; contextos de los refs de tipo del proyecto. */
        final long[] refType = new long[4], refField = new long[4], refContexts = new long[CONTEXTS.length];
        final TreeMap<String, Long> refUnresolvedByReason = new TreeMap<>();
        long refRecords, refSelf, fields;

        Stats() { for (String o : ORIGINS) calls.put(o, 0L); }

        synchronized void merge(Stats b) {
            parseMs += b.parseMs; analyzeMs += b.analyzeMs; scanMs += b.scanMs;
            files += b.files; errors += b.errors; warnings += b.warnings; originFallback += b.originFallback;
            b.calls.forEach((k, v) -> calls.merge(k, v, Long::sum));
            b.unresolvedByReason.forEach((k, v) -> unresolvedByReason.merge(k, v, Long::sum));
            missingPackages.addAll(b.missingPackages);
            for (int i = 0; i < 4; i++) { refType[i] += b.refType[i]; refField[i] += b.refField[i]; }
            for (int i = 0; i < CONTEXTS.length; i++) refContexts[i] += b.refContexts[i];
            b.refUnresolvedByReason.forEach((k, v) -> refUnresolvedByReason.merge(k, v, Long::sum));
            refRecords += b.refRecords; refSelf += b.refSelf; fields += b.fields;
        }
    }

    // ------------------------------------------------------------------ ejecucion

    final Request rq;
    final Charset defaultCharset;
    final AtomicInteger parsed = new AtomicInteger(), analyzed = new AtomicInteger(), scanned = new AtomicInteger();
    final Stats total = new Stats();
    int totalFiles;
    List<String> sourceRootsForBatches = List.of();
    final AtomicInteger filterUnavailable = new AtomicInteger();

    JavaAnalyzer(Request rq) throws RequestException {
        this.rq = rq;
        this.defaultCharset = resolveDefaultEncoding(rq.defaultEncoding, rq.root);
    }

    int run() throws Exception {
        long t0 = System.nanoTime();
        Thread watch = new Thread(() -> {
            while (true) {
                sampleHeap();
                try { Thread.sleep(200); } catch (InterruptedException e) { return; }
            }
        }, "heap-watch");
        watch.setDaemon(true);
        watch.start();

        JavaCompiler jc = ToolProvider.getSystemJavaCompiler();
        if (jc == null) throw new CompilerStartException("no hay compilador del sistema (\u00bfJRE en lugar de JDK?)", null);

        Files.createDirectories(rq.out);
        Path result = rq.out.resolve(RESULT_NAME), tmp = rq.out.resolve(RESULT_NAME + ".tmp");
        Files.deleteIfExists(result);

        List<String> files = new ArrayList<>(new LinkedHashSet<>(rq.files));
        totalFiles = files.size();
        List<List<String>> batches = new ArrayList<>();
        if (rq.batchSize <= 0 || rq.batchSize >= files.size()) batches.add(files);
        else for (int i = 0; i < files.size(); i += rq.batchSize) batches.add(files.subList(i, Math.min(files.size(), i + rq.batchSize)));
        log("JavaAnalyzer " + ANALYZER_VERSION + " sobre " + rq.root + ": " + totalFiles + " ficheros, " + batches.size()
                + " lote(s), classpath " + rq.classpath.size() + " entradas, java " + System.getProperty("java.version")
                + ", heap m\u00e1x " + Runtime.getRuntime().maxMemory() / 1048576 + " MB");
        if (batches.size() > 1) {
            sourceRootsForBatches = rq.sourceRoots.isEmpty() ? computeSourceRoots(files) : rq.sourceRoots;
            log("sourcepath de los lotes: " + sourceRootsForBatches);
        }
        progress("parse", 0, totalFiles);

        try (Writer w = new BufferedWriter(new OutputStreamWriter(Files.newOutputStream(tmp), StandardCharsets.UTF_8), 1 << 16)) {
            if (batches.size() == 1) {
                total.merge(runBatch(jc, batches.get(0), w));
            } else {
                int par = Math.min(rq.maxParallelBatches, batches.size());
                ExecutorService ex = Executors.newFixedThreadPool(par);
                List<Path> parts = new ArrayList<>();
                List<Future<Stats>> futs = new ArrayList<>();
                for (int i = 0; i < batches.size(); i++) {
                    Path part = rq.out.resolve(RESULT_NAME + ".part" + i);
                    parts.add(part);
                    List<String> b = batches.get(i);
                    futs.add(ex.submit(() -> {
                        try (Writer pw = new BufferedWriter(new OutputStreamWriter(Files.newOutputStream(part), StandardCharsets.UTF_8), 1 << 16)) {
                            return runBatch(jc, b, pw);
                        }
                    }));
                }
                ex.shutdown();
                try {
                    for (Future<Stats> f : futs) total.merge(f.get());
                } catch (ExecutionException e) {
                    ex.shutdownNow();
                    Throwable c = e.getCause();
                    if (c instanceof Exception) throw (Exception) c;
                    if (c instanceof Error) throw (Error) c;
                    throw e;
                } finally {
                    w.flush();
                }
                for (Path part : parts) {
                    try (Reader r = new InputStreamReader(Files.newInputStream(part), StandardCharsets.UTF_8)) { r.transferTo(w); }
                    Files.delete(part);
                }
            }
            sampleHeap();
            w.write(summary(System.nanoTime() - t0, batches.size()));
            w.write('\n');
        }
        try {
            Files.move(tmp, result, StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        } catch (AtomicMoveNotSupportedException e) {
            Files.move(tmp, result, StandardCopyOption.REPLACE_EXISTING);
        }
        long wall = (System.nanoTime() - t0) / 1000000;
        long resolved = total.calls.get("project") + total.calls.get("jdk") + total.calls.get("jar");
        long all = resolved + total.calls.get("unresolved");
        log(String.format(Locale.ROOT, "terminado en %d ms: %d ficheros, llamadas %s (%.3f %% resueltas), errores %d, heap pico %d MB%s",
                wall, total.files, total.calls, all == 0 ? 100.0 : 100.0 * resolved / all, total.errors, heapPeak / 1048576,
                total.missingPackages.isEmpty() ? "" : ", paquetes que faltan " + total.missingPackages));
        log(String.format(Locale.ROOT, "referencias: tipos %s, campos %s, %d registros ref, %d registros field, %d autorreferencias excluidas",
                originsJson(total.refType), originsJson(total.refField), total.refRecords, total.fields, total.refSelf));
        if (total.originFallback > 0) log("aviso: " + total.originFallback + " destinos sin fichero de origen clasificados como jdk");
        return 0;
    }

    static void sampleHeap() {
        long u = ManagementFactory.getMemoryMXBean().getHeapMemoryUsage().getUsed();
        if (u > heapPeak) heapPeak = u;
    }

    String summary(long wallNs, int batches) {
        StringBuilder calls = new StringBuilder("{");
        total.calls.forEach((k, v) -> { if (calls.length() > 1) calls.append(','); J.str(calls, k); calls.append(':').append(v); });
        calls.append('}');
        StringBuilder unres = new StringBuilder("{");
        total.unresolvedByReason.forEach((k, v) -> { if (unres.length() > 1) unres.append(','); J.str(unres, k); unres.append(':').append(v); });
        unres.append('}');
        StringBuilder miss = new StringBuilder();
        J.arr(miss, total.missingPackages);
        return new J().s("k", "summary").n("schema", SCHEMA).s("analyzerVersion", ANALYZER_VERSION)
                .s("javaVersion", System.getProperty("java.version")).n("files", total.files)
                .n("parseMs", total.parseMs).n("analyzeMs", total.analyzeMs).n("scanMs", total.scanMs)
                .n("heapPeakMb", heapPeak / 1048576).raw("calls", calls.toString()).raw("unresolvedByReason", unres.toString())
                .raw("missingPackages", miss.toString())
                .raw("diagnostics", "{\"error\":" + total.errors + ",\"warning\":" + total.warnings + "}")
                .raw("refs", refsJson()).n("fields", total.fields)
                .n("batches", batches).n("wallMs", wallNs / 1000000).end();
    }

    static String originsJson(long[] v) {
        StringBuilder sb = new StringBuilder("{");
        for (int i = 0; i < ORIGINS.length; i++) { if (i > 0) sb.append(','); J.str(sb, ORIGINS[i]); sb.append(':').append(v[i]); }
        return sb.append('}').toString();
    }

    String refsJson() {
        StringBuilder ctx = new StringBuilder("{");
        for (int i = 0; i < CONTEXTS.length; i++) { if (i > 0) ctx.append(','); J.str(ctx, CONTEXTS[i]); ctx.append(':').append(total.refContexts[i]); }
        ctx.append('}');
        StringBuilder unres = new StringBuilder("{");
        total.refUnresolvedByReason.forEach((k, v) -> { if (unres.length() > 1) unres.append(','); J.str(unres, k); unres.append(':').append(v); });
        unres.append('}');
        return new J().raw("type", originsJson(total.refType)).raw("field", originsJson(total.refField))
                .raw("contexts", ctx.toString()).n("records", total.refRecords).n("selfExcluded", total.refSelf)
                .raw("unresolvedByReason", unres.toString()).end();
    }

    /** Raices de fuentes para -sourcepath: la carpeta del fichero sin los segmentos de su package. */
    List<String> computeSourceRoots(List<String> files) {
        LinkedHashSet<String> roots = new LinkedHashSet<>();
        for (String rel : files) {
            Path abs = rq.root.resolve(rel).normalize();
            Path dir = abs.getParent();
            if (dir == null) continue;
            String pkg;
            try {
                pkg = packageOf(decode(Files.readAllBytes(abs), defaultCharset).text);
            } catch (IOException e) {
                continue;
            }
            Path r = dir;
            if (pkg != null && !pkg.isEmpty()) {
                String[] seg = pkg.split("\\.");
                boolean ok = true;
                for (int i = seg.length - 1; i >= 0 && ok; i--) {
                    if (r == null || r.getFileName() == null || !r.getFileName().toString().equals(seg[i])) ok = false;
                    else r = r.getParent();
                }
                if (!ok || r == null) continue;
            }
            roots.add(r.toString());
        }
        return new ArrayList<>(roots);
    }

    /** Nombre del package del fichero: primer token tras comentarios y espacios; null si no hay (o hay anotaciones). */
    static String packageOf(String s) {
        int i = 0, n = s.length();
        while (i < n) {
            char c = s.charAt(i);
            if (Character.isWhitespace(c) || c == '\ufeff') { i++; continue; }
            if (s.startsWith("//", i)) { int e = s.indexOf('\n', i); i = e < 0 ? n : e + 1; continue; }
            if (s.startsWith("/*", i)) { int e = s.indexOf("*/", i + 2); i = e < 0 ? n : e + 2; continue; }
            break;
        }
        if (!s.startsWith("package", i) || i + 7 >= n || Character.isJavaIdentifierPart(s.charAt(i + 7))) return null;
        int e = s.indexOf(';', i);
        if (e < 0) return null;
        return s.substring(i + 7, e).replaceAll("\\s+|/\\*.*?\\*/", "");
    }

    // ------------------------------------------------------------------ un lote

    static final Pattern MISSING_PKG = Pattern.compile("package (\\S+) does not exist");

    Stats runBatch(JavaCompiler jc, List<String> rels, Writer w) throws Exception {
        Stats st = new Stats();
        boolean batched = rq.batchSize > 0 && rels.size() < totalFiles;
        Map<URI, SrcFile> byUri = new HashMap<>();
        List<SrcFile> units = new ArrayList<>();
        for (String rel : rels) {
            Path abs = rq.root.resolve(rel).normalize();
            try {
                Decoded d = decode(Files.readAllBytes(abs), defaultCharset);
                SrcFile f = new SrcFile(abs.toUri(), rel, d.text, d.encoding);
                units.add(f);
                byUri.put(f.toUri(), f);
            } catch (IOException e) {
                st.errors++;
                w.write(new J().s("k", "diag").s("path", rel).lineNo("line", 0).s("msg", "cannot read file: " + e).end());
                w.write('\n');
            }
        }

        Map<String, Integer> errorsByFile = new HashMap<>();
        Map<String, List<String>> diagsByFile = new HashMap<>();
        List<String> orphanDiags = new ArrayList<>();
        DiagnosticListener<JavaFileObject> dl = d -> {
            Diagnostic.Kind kind = d.getKind();
            boolean err = kind == Diagnostic.Kind.ERROR;
            boolean warn = kind == Diagnostic.Kind.WARNING || kind == Diagnostic.Kind.MANDATORY_WARNING;
            if (!err && !warn) return;
            String msg = d.getMessage(Locale.ROOT);
            if (err && "compiler.err.doesnt.exist".equals(d.getCode())) {
                Matcher m = MISSING_PKG.matcher(msg);
                if (m.find()) synchronized (st) { st.missingPackages.add(m.group(1)); }
            }
            JavaFileObject src = d.getSource();
            SrcFile sf = src == null ? null : byUri.get(src.toUri());
            synchronized (st) {
                if (src != null && sf == null) return; // fichero cargado por -sourcepath: lo cuenta su propio lote
                if (warn) { st.warnings++; return; }
                st.errors++;
                long line = d.getLineNumber();
                String rec = new J().s("k", "diag").s("path", sf == null ? null : sf.rel).lineNo("line", line == Diagnostic.NOPOS ? 0 : line)
                        .s("msg", msg).end();
                if (sf == null) orphanDiags.add(rec);
                else {
                    errorsByFile.merge(sf.rel, 1, Integer::sum);
                    diagsByFile.computeIfAbsent(sf.rel, k -> new ArrayList<>()).add(rec);
                }
            }
        };

        StandardJavaFileManager fm = jc.getStandardFileManager(dl, Locale.ROOT, defaultCharset);
        List<String> opts = new ArrayList<>(Arrays.asList("--release", "8", "-proc:none", "-implicit:none",
                "-Xmaxerrs", "1000000", "-nowarn", "-XDshould-stop.ifError=FLOW", "-encoding", defaultCharset.name()));
        if (!rq.classpath.isEmpty()) { opts.add("-classpath"); opts.add(String.join(File.pathSeparator, rq.classpath)); }
        else { opts.add("-classpath"); opts.add(""); }
        if (batched) { opts.add("-sourcepath"); opts.add(String.join(File.pathSeparator, sourceRootsForBatches)); }
        else { opts.add("-sourcepath"); opts.add(""); }
        JavaFileManager jfm = batched ? new DecodingFileManager(fm, defaultCharset) : fm;
        JavacTask task;
        try {
            task = (JavacTask) jc.getTask(null, jfm, dl, opts, null, units);
        } catch (RuntimeException e) {
            throw new CompilerStartException("getTask: " + e.getMessage(), e);
        }

        long t = System.nanoTime();
        List<CompilationUnitTree> cus = new ArrayList<>();
        for (CompilationUnitTree cu : task.parse()) cus.add(cu);
        st.parseMs = (System.nanoTime() - t) / 1000000;
        progress("parse", parsed.addAndGet(units.size()), totalFiles);
        sampleHeap();

        t = System.nanoTime();
        if (batched) analyzeOnly(task);
        else task.analyze();
        st.analyzeMs = (System.nanoTime() - t) / 1000000;
        progress("analyze", analyzed.addAndGet(units.size()), totalFiles);
        sampleHeap();

        t = System.nanoTime();
        for (String rec : orphanDiags) { w.write(rec); w.write('\n'); }
        Trees trees = Trees.instance(task);
        Ctx ctx = new Ctx(trees, task.getElements(), task.getTypes(), st, w);
        for (CompilationUnitTree cu : cus) {
            JavaFileObject src = cu.getSourceFile();
            SrcFile sf = byUri.get(src.toUri());
            if (sf == null) continue;
            st.files++;
            w.write(new J().s("k", "file").s("path", sf.rel).s("encoding", sf.encoding).n("errors", errorsByFile.getOrDefault(sf.rel, 0)).end());
            w.write('\n');
            for (String rec : diagsByFile.getOrDefault(sf.rel, List.of())) { w.write(rec); w.write('\n'); }
            FileScan fs = new FileScan(ctx, cu, sf.rel);
            fs.scan(cu, null);
            fs.flushRefs();
            int s = scanned.incrementAndGet();
            if (s % PROGRESS_EVERY == 0 || s == totalFiles) progress("scan", s, totalFiles);
        }
        st.scanMs = (System.nanoTime() - t) / 1000000;
        sampleHeap();
        for (SrcFile f : units) f.text = null;
        jfm.close();
        return st;
    }

    /** En lotes: atribuye solo las clases del lote (JavacTaskImpl.analyze(Iterable)), si el export de javac.api lo permite. */
    void analyzeOnly(JavacTask task) throws Exception {
        try {
            Method enter = task.getClass().getMethod("enter");
            Method analyze = task.getClass().getMethod("analyze", Iterable.class);
            Object entered = enter.invoke(task);
            analyze.invoke(task, entered);
        } catch (IllegalAccessException | NoSuchMethodException | RuntimeException e) {
            if (filterUnavailable.getAndIncrement() == 0)
                log("aviso: sin --add-exports jdk.compiler/com.sun.tools.javac.api=ALL-UNNAMED cada lote atribuye tambi\u00e9n "
                        + "los ficheros de -sourcepath (" + e + ")");
            task.analyze();
        } catch (InvocationTargetException e) {
            Throwable c = e.getCause();
            if (c instanceof Exception) throw (Exception) c;
            if (c instanceof Error) throw (Error) c;
            throw e;
        }
    }

    // ------------------------------------------------------------------ recorrido

    static final class Ctx {
        final Trees trees;
        final SourcePositions sp;
        final Elements els;
        final Types types;
        final Stats st;
        final Writer w;
        /** Por lote (un hilo): nombre y origen de cada tipo destino, y clave ownerFqn#nombre de cada campo destino. */
        final IdentityHashMap<Element, TInfo> tinfo = new IdentityHashMap<>();
        final IdentityHashMap<Element, String> fkeys = new IdentityHashMap<>();
        Ctx(Trees trees, Elements els, Types types, Stats st, Writer w) {
            this.trees = trees; this.sp = trees.getSourcePositions(); this.els = els; this.types = types; this.st = st; this.w = w;
        }

        TInfo typeInfo(TypeElement te) {
            TInfo i = tinfo.get(te);
            if (i == null) {
                // Tipo que javac no encuentra: su owner puede ser noSymbol y classifyType lo tomaria por jdk.
                boolean err = te.asType() == null || te.asType().getKind() == TypeKind.ERROR;
                tinfo.put(te, i = new TInfo(ownerName(els, te), err ? O_UNRESOLVED : originIndex(classifyType(te, st))));
            }
            return i;
        }

        String fieldKey(VariableElement ve, TInfo owner) {
            String k = fkeys.get(ve);
            if (k == null) fkeys.put(ve, k = owner.name + "#" + ve.getSimpleName());
            return k;
        }
    }

    static final class TInfo {
        final String name;
        final int origin;
        TInfo(String n, int o) { name = n; origin = o; }
    }

    /** Referencias agregadas de un fichero por (origen, destino, kind, access) (Q2). */
    static final class Agg {
        final String from, fromField, fromType, to, kind, access;
        int ctx, count, n;
        int[] lines = new int[4];
        Agg(String from, String fromField, String fromType, String to, String kind, String access) {
            this.from = from; this.fromField = fromField; this.fromType = fromType; this.to = to; this.kind = kind; this.access = access;
        }
        void addLine(int l) {
            if (n > 0 && lines[n - 1] == l) return;
            if (n == lines.length) lines = Arrays.copyOf(lines, n * 2);
            lines[n++] = l;
        }
        String linesJson() {
            int[] a = Arrays.copyOf(lines, n);
            Arrays.sort(a);
            StringBuilder sb = new StringBuilder(n * 5 + 2).append('[');
            for (int i = 0; i < a.length; i++) {
                if (i > 0 && a[i] == a[i - 1]) continue;
                if (sb.length() > 1) sb.append(',');
                sb.append(a[i]);
            }
            return sb.append(']').toString();
        }
    }

    static String contextsJson(int mask) {
        StringBuilder sb = new StringBuilder("[");
        for (int i = 0; i < CONTEXTS.length; i++) {
            if ((mask & (1 << i)) == 0) continue;
            if (sb.length() > 1) sb.append(',');
            J.str(sb, CONTEXTS[i]);
        }
        return sb.append(']').toString();
    }

    static final class FileScan extends TreePathScanner<Void, Void> {
        final Ctx c;
        final CompilationUnitTree cu;
        final String rel;
        final LineMap lm;
        final ArrayList<String> chain = new ArrayList<>();
        /** true mientras el contexto lexico es un tipo con nombre (o la unidad): fuera de clases anonimas y locales. */
        boolean inNamed = true;
        /** Origen de llamadas y referencias: metodo (from), campo (fromField, solo refs) y tipo con nombre (fromType). */
        String from = null, fromType = null, fromField = null;
        long bodyStart = -1;
        final LinkedHashMap<String, Agg> refs = new LinkedHashMap<>();

        FileScan(Ctx c, CompilationUnitTree cu, String rel) {
            this.c = c; this.cu = cu; this.rel = rel; this.lm = cu.getLineMap();
        }

        long line(long pos) { return pos < 0 ? 0 : lm.getLineNumber(pos); }
        long startLine(Tree t) { return line(c.sp.getStartPosition(cu, t)); }
        long endLine(Tree t) {
            long e = c.sp.getEndPosition(cu, t);
            return e <= 0 ? startLine(t) : line(e - 1);
        }

        void emit(String rec) {
            try { c.w.write(rec); c.w.write('\n'); } catch (IOException e) { throw new UncheckedIOException(e); }
        }

        // --- tipos

        @Override public Void visitClass(ClassTree t, Void v) {
            Tree parent = getCurrentPath().getParentPath().getLeaf();
            Element el = c.trees.getElement(getCurrentPath());
            boolean named = inNamed && (parent instanceof CompilationUnitTree || parent instanceof ClassTree) && el instanceof TypeElement;
            if (!named) {
                boolean saved = inNamed;
                inNamed = false;
                try { return super.visitClass(t, v); } finally { inNamed = saved; }
            }
            TypeElement te = (TypeElement) el;
            chain.add(t.getSimpleName().toString());
            String sFrom = from, sType = fromType, sField = fromField;
            long sBody = bodyStart;
            try {
                emitType(t, te);
                from = null;
                fromField = null;
                fromType = te.getQualifiedName().toString();
                return super.visitClass(t, v);
            } finally {
                from = sFrom; fromType = sType; fromField = sField; bodyStart = sBody;
                chain.remove(chain.size() - 1);
            }
        }

        void emitType(ClassTree t, TypeElement te) {
            String kind;
            switch (te.getKind()) {
                case INTERFACE: kind = "interface"; break;
                case ENUM: kind = "enum"; break;
                case ANNOTATION_TYPE: kind = "annotation"; break;
                default: kind = "class";
            }
            Set<Modifier> mods = te.getModifiers();
            J j = new J().s("k", "type").s("path", rel).arr("chain", chain).s("fqn", te.getQualifiedName()).s("kind", kind)
                    .lineNo("line", startLine(t)).lineNo("end", endLine(t)).s("access", access(mods))
                    .b("abstract", mods.contains(Modifier.ABSTRACT)).arr("annotations", annotations(t.getModifiers()))
                    .s("doc", doc(te));
            // En javac las interfaces extendidas de una interfaz van en getImplementsClause: salen en "interfaces".
            if (t.getKind() == Tree.Kind.CLASS && t.getExtendsClause() != null) j.raw("super", superRef(t.getExtendsClause()));
            else j.raw("super", "null");
            StringBuilder ifs = new StringBuilder("[");
            for (Tree i : t.getImplementsClause()) { if (ifs.length() > 1) ifs.append(','); ifs.append(superRef(i)); }
            j.raw("interfaces", ifs.append(']').toString());
            emit(j.end());
        }

        String superRef(Tree clause) {
            TypeMirror tm = c.trees.getTypeMirror(new TreePath(getCurrentPath(), clause));
            String fqn, origin;
            if (tm != null && tm.getKind() == TypeKind.DECLARED && ((DeclaredType) tm).asElement() instanceof TypeElement) {
                TypeElement st = (TypeElement) ((DeclaredType) tm).asElement();
                fqn = ownerName(c.els, st);
                origin = classifyType(st, c.st);
            } else {
                fqn = stripGenerics(clause.toString());
                origin = "unresolved";
            }
            return new J().s("fqn", fqn).s("origin", origin).end();
        }

        // --- metodos

        @Override public Void visitMethod(MethodTree t, Void v) {
            Element el = c.trees.getElement(getCurrentPath());
            ExecutableElement e = el instanceof ExecutableElement ? (ExecutableElement) el : null;
            if (e != null && isGenerated(e)) return null; // constructor por defecto o de clase anonima que anade javac
            String sFrom = from, sField = fromField;
            long sBody = bodyStart;
            bodyStart = t.getBody() == null ? -1 : c.sp.getStartPosition(cu, t.getBody());
            try {
                if (inNamed && e != null) { from = emitMethod(t, e); fromField = null; }
                return super.visitMethod(t, v);
            } finally {
                from = sFrom; fromField = sField; bodyStart = sBody;
            }
        }

        String emitMethod(MethodTree t, ExecutableElement e) {
            TypeElement owner = (TypeElement) e.getEnclosingElement();
            boolean ctor = e.getKind() == ElementKind.CONSTRUCTOR;
            String key = key(c.els, c.types, e);
            List<String> params = new ArrayList<>(), simple = new ArrayList<>(), names = new ArrayList<>();
            for (VariableElement p : e.getParameters()) {
                String er = erased(c.types, p.asType());
                params.add(er);
                simple.add(simpleOf(er));
                names.add(p.getSimpleName().toString());
            }
            String ret = ctor ? null : erased(c.types, e.getReturnType());
            Set<Modifier> mods = e.getModifiers();
            J j = new J().s("k", "method").s("path", rel).arr("chain", chain).s("ownerFqn", owner.getQualifiedName())
                    .s("name", ctor ? owner.getSimpleName() : e.getSimpleName()).b("ctor", ctor)
                    .lineNo("line", startLine(t)).lineNo("end", endLine(t)).s("key", key)
                    .arr("params", params).arr("paramSimple", simple).arr("paramNames", names)
                    .s("ret", ret).s("retSimple", ret == null ? null : simpleOf(ret)).s("access", access(mods))
                    .b("static", mods.contains(Modifier.STATIC)).b("abstract", mods.contains(Modifier.ABSTRACT))
                    .arr("annotations", annotations(t.getModifiers())).s("doc", doc(e))
                    .n("cyclomatic", cyclomatic(t));
            emit(j.end());
            return key;
        }

        String doc(Element e) {
            String d = c.els.getDocComment(e);
            if (d == null) return null;
            d = d.strip();
            return d.isEmpty() ? null : d;
        }

        // --- llamadas

        @Override public Void visitMethodInvocation(MethodInvocationTree t, Void v) {
            ExpressionTree ms = t.getMethodSelect();
            if (ms instanceof IdentifierTree && ((IdentifierTree) ms).getName().contentEquals("super")
                    && bodyStart >= 0 && c.sp.getStartPosition(cu, t) == bodyStart) {
                return super.visitMethodInvocation(t, v); // super() implicito que inserta javac
            }
            long end = c.sp.getEndPosition(cu, ms);
            long ln = end > 0 ? line(end - 1) : startLine(t);
            emitCall(ln, c.trees.getElement(getCurrentPath()), "call");
            return super.visitMethodInvocation(t, v);
        }

        @Override public Void visitNewClass(NewClassTree t, Void v) {
            TreePath parentPath = getCurrentPath().getParentPath();
            if (parentPath.getLeaf() instanceof VariableTree) {
                Element pe = c.trees.getElement(parentPath);
                if (pe != null && pe.getKind() == ElementKind.ENUM_CONSTANT) return super.visitNewClass(t, v);
            }
            Element e = c.trees.getElement(getCurrentPath());
            if (t.getClassBody() != null) {
                Element sup = anonSuperConstructor(getCurrentPath(), t);
                if (sup != null) e = sup;
            }
            emitCall(startLine(t), e, "new");
            return super.visitNewClass(t, v);
        }

        /** En una clase anonima el destino util es el constructor del supertipo que invoca el constructor generado. */
        Element anonSuperConstructor(TreePath newPath, NewClassTree t) {
            ClassTree body = t.getClassBody();
            TreePath bodyPath = new TreePath(newPath, body);
            for (Tree m : body.getMembers()) {
                if (!(m instanceof MethodTree)) continue;
                MethodTree mt = (MethodTree) m;
                TreePath mp = new TreePath(bodyPath, mt);
                Element me = c.trees.getElement(mp);
                if (!(me instanceof ExecutableElement) || me.getKind() != ElementKind.CONSTRUCTOR || mt.getBody() == null) continue;
                for (StatementTree s : mt.getBody().getStatements()) {
                    if (s instanceof ExpressionStatementTree && ((ExpressionStatementTree) s).getExpression() instanceof MethodInvocationTree) {
                        TreePath sp = new TreePath(new TreePath(new TreePath(mp, mt.getBody()), s), ((ExpressionStatementTree) s).getExpression());
                        Element se = c.trees.getElement(sp);
                        if (se instanceof ExecutableElement && se.getKind() == ElementKind.CONSTRUCTOR) return se;
                    }
                    break;
                }
            }
            return null;
        }

        void emitCall(long line, Element e, String kind) {
            String to = null, origin, reason = null;
            if (e == null) {
                origin = "unresolved"; reason = "null-element";
            } else if (e.getKind() != ElementKind.METHOD && e.getKind() != ElementKind.CONSTRUCTOR) {
                origin = "unresolved"; reason = e.getKind().name();
            } else {
                Element owner = e.getEnclosingElement();
                to = key(c.els, c.types, (ExecutableElement) e);
                if (!(owner instanceof TypeElement) || owner.asType().getKind() == TypeKind.ERROR) {
                    origin = "unresolved"; reason = "error-owner";
                } else {
                    origin = classifyType((TypeElement) owner, c.st);
                    if (origin.equals("unresolved")) reason = "error-owner";
                }
            }
            c.st.calls.merge(origin, 1L, Long::sum);
            if (reason != null) c.st.unresolvedByReason.merge(reason, 1L, Long::sum);
            emit(new J().s("k", "call").s("path", rel).lineNo("line", line).s("from", from).s("fromType", fromType)
                    .s("to", to).s("origin", origin).s("kind", kind).s("reason", reason).end());
        }

        // --- campos y referencias (schema 2)

        @Override public Void visitImport(ImportTree t, Void v) { return null; } // un import no es un uso (Q3)

        @Override public Void visitVariable(VariableTree t, Void v) {
            TreePath path = getCurrentPath();
            if (inNamed && path.getParentPath().getLeaf() instanceof ClassTree) {
                Element el = c.trees.getElement(path);
                if (el != null && (el.getKind() == ElementKind.FIELD || el.getKind() == ElementKind.ENUM_CONSTANT)
                        && el.getEnclosingElement() instanceof TypeElement) {
                    String sFrom = from, sField = fromField;
                    fromField = emitField(t, (VariableElement) el);
                    from = null;
                    try { return super.visitVariable(t, v); } finally { from = sFrom; fromField = sField; }
                }
            }
            return super.visitVariable(t, v);
        }

        String emitField(VariableTree t, VariableElement ve) {
            TypeElement owner = (TypeElement) ve.getEnclosingElement();
            Set<Modifier> mods = ve.getModifiers();
            emit(new J().s("k", "field").s("path", rel).arr("chain", chain).s("ownerFqn", owner.getQualifiedName())
                    .s("name", ve.getSimpleName()).lineNo("line", startLine(t)).lineNo("end", endLine(t))
                    .s("typeFqn", erased(c.types, ve.asType())).s("access", access(mods))
                    .b("static", mods.contains(Modifier.STATIC)).b("final", mods.contains(Modifier.FINAL))
                    .b("enumConstant", ve.getKind() == ElementKind.ENUM_CONSTANT).s("doc", doc(ve)).end());
            c.st.fields++;
            return c.fieldKey(ve, c.typeInfo(owner));
        }

        @Override public Void visitIdentifier(IdentifierTree t, Void v) {
            Name n = t.getName();
            if (!n.contentEquals("this") && !n.contentEquals("super")) {
                long end = c.sp.getEndPosition(cu, t);
                if (end > 0) reference(t, startLine(t));
            }
            return super.visitIdentifier(t, v);
        }

        @Override public Void visitMemberSelect(MemberSelectTree t, Void v) {
            Name n = t.getIdentifier();
            if (!n.contentEquals("class") && !n.contentEquals("this") && !n.contentEquals("super")) {
                long end = c.sp.getEndPosition(cu, t);
                if (end > 0) reference(t, line(end - 1)); // linea del nombre, como en las llamadas
            }
            return super.visitMemberSelect(t, v);
        }

        /** Un identificador o seleccion con posicion final (los arboles que sintetiza javac no la tienen). */
        void reference(ExpressionTree t, long ln) {
            if (fromType == null) return; // anotaciones de package-info
            TreePath path = getCurrentPath();
            Tree parent = path.getParentPath().getLeaf();
            if (parent instanceof MethodInvocationTree && ((MethodInvocationTree) parent).getMethodSelect() == t) return; // es un call
            Element el = c.trees.getElement(path);
            if (el == null) {
                c.st.refType[O_UNRESOLVED]++;
                c.st.refUnresolvedByReason.merge("null-element", 1L, Long::sum);
                return;
            }
            ElementKind k = el.getKind();
            if ((k.isClass() || k.isInterface()) && el instanceof TypeElement) typeRef((TypeElement) el, path, ln);
            else if (k == ElementKind.FIELD || k == ElementKind.ENUM_CONSTANT) fieldRef((VariableElement) el, path, ln);
        }

        void typeRef(TypeElement te, TreePath path, long ln) {
            int ctx = typeContext(path, te);
            if (ctx == 0) return;
            TInfo ti = c.typeInfo(te);
            if (ti.origin == O_PROJECT && ti.name.equals(fromType)) { c.st.refSelf++; return; } // Q3
            c.st.refType[ti.origin]++;
            if (ti.origin == O_UNRESOLVED) { c.st.refUnresolvedByReason.merge("error-type", 1L, Long::sum); return; }
            if (ti.origin != O_PROJECT) return;
            for (int i = 0; i < CONTEXTS.length; i++) if ((ctx & (1 << i)) != 0) c.st.refContexts[i]++;
            addRef(ti.name, "type", null, ctx, ln);
        }

        void fieldRef(VariableElement ve, TreePath path, long ln) {
            Element owner = ve.getEnclosingElement();
            if (!(owner instanceof TypeElement) || isArrayPseudoClass(owner)) return; // length de un array
            TInfo ti = c.typeInfo((TypeElement) owner);
            c.st.refField[ti.origin]++;
            if (ti.origin == O_UNRESOLVED) { c.st.refUnresolvedByReason.merge("error-owner", 1L, Long::sum); return; }
            if (ti.origin != O_PROJECT) return;
            addRef(c.fieldKey(ve, ti), "field", ACCESS[accessOf(path)], 0, ln);
        }

        void addRef(String to, String kind, String access, int ctx, long ln) {
            String k = from + '\u0001' + fromField + '\u0001' + fromType + '\u0001' + to + '\u0001' + kind + '\u0001' + access;
            Agg a = refs.get(k);
            if (a == null) refs.put(k, a = new Agg(from, fromField, fromType, to, kind, access));
            a.ctx |= ctx;
            a.count++;
            a.addLine((int) ln);
        }

        void flushRefs() {
            for (Agg a : refs.values()) {
                emit(new J().s("k", "ref").s("path", rel).s("from", a.from).s("fromType", a.fromType).s("fromField", a.fromField)
                        .s("to", a.to).s("kind", a.kind).s("access", a.access).raw("contexts", contextsJson(a.ctx))
                        .s("origin", "project").n("count", a.count).raw("lines", a.linesJson()).end());
                c.st.refRecords++;
            }
            refs.clear();
        }

        /**
         * Mascara de contextos de un nombre de tipo, subiendo por el arbol hasta la construccion que lo usa; 0 = no se emite
         * (extends/implements, calificador de otro nombre de tipo, new con constructor explicito, que ya es un call).
         */
        int typeContext(TreePath path, TypeElement te) {
            int g = 0;
            Tree child = path.getLeaf();
            for (TreePath p = path.getParentPath(); p != null; child = p.getLeaf(), p = p.getParentPath()) {
                Tree par = p.getLeaf();
                switch (par.getKind()) {
                    case PARAMETERIZED_TYPE:
                        if (((ParameterizedTypeTree) par).getType() != child) g = 1 << C_GENERIC;
                        continue;
                    case ARRAY_TYPE: case UNION_TYPE: case INTERSECTION_TYPE: case ANNOTATED_TYPE:
                    case EXTENDS_WILDCARD: case SUPER_WILDCARD: case UNBOUNDED_WILDCARD:
                        continue;
                    case ANNOTATION: case TYPE_ANNOTATION:
                        return (((AnnotationTree) par).getAnnotationType() == child ? 1 << C_ANNOTATION : 1 << C_OTHER) | g;
                    case MEMBER_SELECT: {
                        Name n = ((MemberSelectTree) par).getIdentifier();
                        if (n.contentEquals("class")) return (1 << C_CLASS_LITERAL) | g;
                        if (n.contentEquals("this") || n.contentEquals("super")) return 0;
                        if (g != 0) continue; // Outer<X>.Inner: el contexto lo da quien usa Inner
                        Element pe = c.trees.getElement(p);
                        if (pe != null && (pe.getKind().isClass() || pe.getKind().isInterface() || pe.getKind() == ElementKind.PACKAGE)) return 0;
                        return 1 << C_STATIC_ACCESS;
                    }
                    case METHOD_INVOCATION:
                        return 1 << C_GENERIC; // argumento de tipo explicito: X.<T>m()
                    case NEW_CLASS: {
                        NewClassTree nc = (NewClassTree) par;
                        if (nc.getIdentifier() != child || g != 0) return 1 << C_GENERIC;
                        return implicitNew(p, nc, te) ? 1 << C_NEW_IMPLICIT : 0; // Q4
                    }
                    case MEMBER_REFERENCE:
                        return (((MemberReferenceTree) par).getQualifierExpression() == child ? 1 << C_STATIC_ACCESS : 1 << C_GENERIC) | g;
                    case VARIABLE: {
                        if (((VariableTree) par).getType() != child) return (1 << C_OTHER) | g;
                        Element ve = c.trees.getElement(p);
                        ElementKind vk = ve == null ? ElementKind.LOCAL_VARIABLE : ve.getKind();
                        int x;
                        switch (vk) {
                            case FIELD: case ENUM_CONSTANT: x = C_FIELD; break;
                            case PARAMETER: x = C_PARAM; break;
                            case EXCEPTION_PARAMETER: x = C_CATCH; break;
                            default: x = C_LOCAL;
                        }
                        return (1 << x) | g;
                    }
                    case METHOD: {
                        MethodTree mt = (MethodTree) par;
                        if (mt.getReturnType() == child) return (1 << C_RETURN) | g;
                        for (Tree th : mt.getThrows()) if (th == child) return (1 << C_THROWS) | g;
                        return (1 << C_OTHER) | g;
                    }
                    case TYPE_PARAMETER:
                        return 1 << C_GENERIC;
                    case CLASS: case INTERFACE: case ENUM: case ANNOTATION_TYPE:
                        return g; // extends/implements (Q3): solo cuentan sus argumentos de tipo
                    case TYPE_CAST:
                        return (((TypeCastTree) par).getType() == child ? 1 << C_CAST : 1 << C_OTHER) | g;
                    case INSTANCE_OF:
                        return (((InstanceOfTree) par).getType() == child ? 1 << C_INSTANCEOF : 1 << C_OTHER) | g;
                    case NEW_ARRAY:
                        return (1 << C_NEW_ARRAY) | g;
                    default:
                        return (1 << C_OTHER) | g;
                }
            }
            return (1 << C_OTHER) | g;
        }

        /** new cuyo constructor no esta escrito: por defecto, o clase anonima de una interfaz o de un supertipo sin constructor. */
        boolean implicitNew(TreePath newPath, NewClassTree nc, TypeElement te) {
            if (nc.getClassBody() != null) {
                Element sup = anonSuperConstructor(newPath, nc);
                return sup == null || sup.getEnclosingElement() != te || isGenerated((ExecutableElement) sup);
            }
            Element e = c.trees.getElement(newPath);
            return e instanceof ExecutableElement && isGenerated((ExecutableElement) e);
        }

        /** read, write (destino de =) o readwrite (destino de op=, ++ y --), atravesando parentesis. */
        int accessOf(TreePath path) {
            Tree child = path.getLeaf();
            for (TreePath p = path.getParentPath(); p != null; child = p.getLeaf(), p = p.getParentPath()) {
                Tree par = p.getLeaf();
                if (par instanceof ParenthesizedTree) continue;
                if (par instanceof AssignmentTree) return ((AssignmentTree) par).getVariable() == child ? A_WRITE : A_READ;
                if (par instanceof CompoundAssignmentTree) return ((CompoundAssignmentTree) par).getVariable() == child ? A_READWRITE : A_READ;
                switch (par.getKind()) {
                    case PREFIX_INCREMENT: case PREFIX_DECREMENT: case POSTFIX_INCREMENT: case POSTFIX_DECREMENT: return A_READWRITE;
                    default: return A_READ;
                }
            }
            return A_READ;
        }
    }

    /** true para la pseudo-clase Array de javac (propietaria de length y clone), cuyo owner es noSymbol (Kind.NIL). */
    static boolean isArrayPseudoClass(Element e) {
        if (!(e instanceof Symbol)) return false;
        Symbol o = ((Symbol) e).owner;
        return o == null || o.kind == Kinds.Kind.NIL;
    }

    // ------------------------------------------------------------------ utilidades de simbolos

    static boolean isGenerated(ExecutableElement e) {
        if (!(e instanceof Symbol)) return false;
        return (((Symbol) e).flags() & (Flags.GENERATEDCONSTR | Flags.ANONCONSTR)) != 0;
    }

    /** ownerFqn#nombre(FQN borrados), igual en declaracion y destino; el constructor usa el nombre simple de la clase. */
    static String key(Elements els, Types types, ExecutableElement x) {
        Element o = x.getEnclosingElement();
        String owner = o instanceof TypeElement ? ownerName(els, (TypeElement) o) : String.valueOf(o);
        String name = x.getKind() == ElementKind.CONSTRUCTOR ? (o == null ? "<init>" : o.getSimpleName().toString()) : x.getSimpleName().toString();
        StringBuilder sb = new StringBuilder(owner.length() + name.length() + 32).append(owner).append('#').append(name).append('(');
        boolean f = true;
        for (VariableElement p : x.getParameters()) {
            if (!f) sb.append(',');
            f = false;
            sb.append(erased(types, p.asType()));
        }
        return sb.append(')').toString();
    }

    /** Nombre canonico si el tipo lo tiene; si es anonimo o local (o cuelga de uno), el nombre binario (con '$'). */
    static String ownerName(Elements els, TypeElement te) {
        for (Element e = te; e != null && !(e instanceof PackageElement); e = e.getEnclosingElement()) {
            if (!(e instanceof TypeElement)) return els.getBinaryName(te).toString();
            NestingKind nk = ((TypeElement) e).getNestingKind();
            if (nk == NestingKind.ANONYMOUS || nk == NestingKind.LOCAL) return els.getBinaryName(te).toString();
        }
        return te.getQualifiedName().toString();
    }

    static String erased(Types types, TypeMirror t) {
        String s;
        try {
            s = types.erasure(t).toString();
        } catch (RuntimeException e) {
            s = t.toString();
        }
        if (s.indexOf('<') >= 0) s = stripGenerics(s);
        if (s.indexOf('@') >= 0) s = s.replaceAll("@[\\w.$]+(\\([^)]*\\))?\\s*", "").replace("()", "").trim();
        return s;
    }

    static String stripGenerics(String s) {
        StringBuilder sb = new StringBuilder(s.length());
        int depth = 0;
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            if (ch == '<') depth++;
            else if (ch == '>') depth = Math.max(0, depth - 1);
            else if (depth == 0 && !Character.isWhitespace(ch)) sb.append(ch);
        }
        return sb.toString();
    }

    /** Nombre simple del tipo borrado: ultimo segmento, conservando los [] de los arrays. */
    static String simpleOf(String erased) {
        String base = erased, dims = "";
        while (base.endsWith("[]")) { base = base.substring(0, base.length() - 2); dims += "[]"; }
        int dot = base.lastIndexOf('.');
        return (dot >= 0 ? base.substring(dot + 1) : base) + dims;
    }

    static String access(Set<Modifier> m) {
        if (m.contains(Modifier.PUBLIC)) return "public";
        if (m.contains(Modifier.PROTECTED)) return "protected";
        if (m.contains(Modifier.PRIVATE)) return "private";
        return "package";
    }

    static List<String> annotations(ModifiersTree mods) {
        List<String> r = new ArrayList<>();
        if (mods == null) return r;
        for (AnnotationTree a : mods.getAnnotations()) {
            String n = a.getAnnotationType().toString();
            r.add(n.substring(n.lastIndexOf('.') + 1));
        }
        return r;
    }

    /** project = fuente del proyecto; jdk = ct.sym/jrt; jar = cualquier otra entrada del classpath. */
    static String classifyType(TypeElement te, Stats st) {
        Element o = te;
        while (o.getEnclosingElement() != null && o.getEnclosingElement().getKind() != ElementKind.PACKAGE) o = o.getEnclosingElement();
        if (o.asType() != null && o.asType().getKind() == TypeKind.ERROR) return "unresolved";
        if (!(o instanceof Symbol.ClassSymbol)) { st.originFallback++; return "jdk"; } // pseudo-clase Array (clone de arrays)
        Symbol.ClassSymbol cs = (Symbol.ClassSymbol) o;
        JavaFileObject f = cs.classfile != null ? cs.classfile : cs.sourcefile;
        if (f == null) { st.originFallback++; return "jdk"; }
        if (f.getKind() == JavaFileObject.Kind.SOURCE) return "project";
        String u = f.toUri().toString();
        if (u.contains("ct.sym") || u.startsWith("jrt:")) return "jdk";
        return "jar";
    }

    /** 1 + if, for, foreach, while, do, case (no default), catch, ?:, &&, ||; sin entrar en clases anidadas. */
    static int cyclomatic(MethodTree m) {
        if (m.getBody() == null) return 1;
        int[] n = {1};
        new TreeScanner<Void, Void>() {
            @Override public Void visitClass(ClassTree t, Void v) { return null; }
            @Override public Void visitIf(IfTree t, Void v) { n[0]++; return super.visitIf(t, v); }
            @Override public Void visitForLoop(ForLoopTree t, Void v) { n[0]++; return super.visitForLoop(t, v); }
            @Override public Void visitEnhancedForLoop(EnhancedForLoopTree t, Void v) { n[0]++; return super.visitEnhancedForLoop(t, v); }
            @Override public Void visitWhileLoop(WhileLoopTree t, Void v) { n[0]++; return super.visitWhileLoop(t, v); }
            @Override public Void visitDoWhileLoop(DoWhileLoopTree t, Void v) { n[0]++; return super.visitDoWhileLoop(t, v); }
            @Override public Void visitCase(CaseTree t, Void v) { if (!t.getExpressions().isEmpty()) n[0]++; return super.visitCase(t, v); }
            @Override public Void visitCatch(CatchTree t, Void v) { n[0]++; return super.visitCatch(t, v); }
            @Override public Void visitConditionalExpression(ConditionalExpressionTree t, Void v) { n[0]++; return super.visitConditionalExpression(t, v); }
            @Override public Void visitBinary(BinaryTree t, Void v) {
                if (t.getKind() == Tree.Kind.CONDITIONAL_AND || t.getKind() == Tree.Kind.CONDITIONAL_OR) n[0]++;
                return super.visitBinary(t, v);
            }
        }.scan(m.getBody(), null);
        return n[0];
    }
}
