# Proposed DUSMP JVM launch presets

Research date: 26 October 2026. The initial research proposal is retained below.

Implementation update: the launcher now includes three device
presets: **low 8gb** (8 GB total system RAM; G1, 1 GB initial / 4 GB maximum heap),
**low 16gb** (16 GB total system RAM; G1, 1 GB initial / 6 GB maximum heap), and
**Standard 16gb+** (at least 16 GB total system RAM; Generational ZGC, 2 GB initial /
10 GB maximum heap). The names describe system RAM, while the UI separately shows
the smaller Minecraft allocation. The device choice is required during first-run
setup and once for upgrades without a saved choice. All three presets keep the
maximum RAM adjustable in Settings. This supersedes the initial illustrative
4 GB / 8 GB budgets below; those budgets were research starting points rather
than pack benchmark results.

## Pack and runtime checked

- The current live Islands edition is Minecraft 1.21.1, NeoForge 21.1.248, pack version 1.0.0, with 165 mod entries.
- Relevant mods include Create, ModernFix, FerriteCore, Sodium, Lithium, and optional Distant Horizons 3.3.2 and Iris.
- The installed managed runtime is Microsoft OpenJDK 21.0.7, 64-bit. Both G1 and Generational ZGC were accepted by this runtime in separate `java -version` checks with a small test heap. These checks establish flag availability, not Minecraft performance.
- The live manifest has no Java tuning block. The development fixture contains a G1 flag bundle; that fixture is not evidence of optimal settings for the live pack.
- Pre-DUSMP currently has no published version or files. Recheck its Minecraft and Java requirements when it is published.

NeoForge's versioned 1.21.1 documentation requires Java 21 and a 64-bit JVM. Both proposed presets can use the same managed Java 21 runtime. There is no need for two separate Java installations.

Source: [NeoForge 1.21.1 prerequisites](https://docs.neoforged.net/docs/1.21.1/gettingstarted/).

## Proposed starting points

The hardware tiers and heap sizes below are engineering starting points for validation, not documented requirements or measured optimums for DUSMP. Available RAM and CPU headroom matter more than a CPU core count alone. Java heap is only part of Minecraft's total memory usage.

| Preset | Suggested systems | Initial / maximum heap | Collector |
| --- | --- | --- | --- |
| Efficient (low / mid range) | 8–16 GB system RAM, or limited CPU headroom | 1 GB / 4 GB on an 8 GB PC; 1 GB / 6 GB on a 16 GB PC | G1 |
| Smooth (high end) | Preferably 32 GB or more system RAM and a capable modern multicore CPU | 2 GB / 8 GB initially | Generational ZGC |

Illustrative full JVM options for the 8 GB-system Efficient preset:

```text
-Xms1G -Xmx4G -XX:+UseG1GC
```

Illustrative full JVM options for the Smooth preset:

```text
-Xms2G -Xmx8G -XX:+UseZGC -XX:+ZGenerational
```

An 8 GB computer is a constrained test target for this 165-mod pack; 4 GB of heap is not yet established as sufficient. Reduce or disable optional DH work and shaders during low-end validation. Keep the heap adjustable. A 16 GB computer can use Efficient at 6 GB and evaluate higher allocations only if system memory remains available. High-end users should retain a G1 fallback.

For heavy DH use on a machine with ample RAM, test an increase from 8 GB to 10–12 GB only when heap occupancy or allocation stalls justify it. Do not automatically give Minecraft half the machine's RAM or 16 GB just because it has many cores.

### Why G1 for Efficient

G1 balances throughput and pauses, and Oracle recommends starting with its defaults plus the heap size. Broad manual changes to young-generation sizing can reduce the collector's ability to adapt. This is the conservative baseline when Minecraft, chunk workers, and the OS already compete for resources.

Source: [Oracle Java 21 G1 tuning recommendations](https://docs.oracle.com/en/java/javase/21/gctuning/garbage-first-garbage-collector-tuning.html).

### Why Generational ZGC for Smooth

ZGC performs most GC work concurrently, aiming to reduce pauses. It uses CPU resources while the application runs, so fewer GC interruptions do not guarantee higher average FPS or faster startup. It needs sufficient heap space for live objects and allocations while collection proceeds.

The DH author's FAQ specifically suggests ZGC for Java 21+ stuttering, along with increasing memory or reducing DH CPU load, distant generation, and graphics settings. Its RAM ranges are troubleshooting suggestions, not DUSMP-specific measurements.

For Java 21, both `-XX:+UseZGC` and `-XX:+ZGenerational` are needed to select Generational ZGC. No experimental unlock is needed. Do not blindly reuse that exact syntax on another Java major release.

Sources: [Oracle's Generational ZGC explanation](https://inside.java/2023/11/28/gen-zgc-explainer/), [Java 21 release notes](https://www.oracle.com/java/technologies/javase/21-relnote-issues.html), [ZGC heap headroom](https://docs.oracle.com/en/java/javase/21/gctuning/z-garbage-collector2.html), [Distant Horizons author's FAQ](https://modrinth.com/mod/distanthorizons?loader=neoforge).

## Options excluded from the initial presets

- No forced `G1HeapRegionSize=32M` or young-generation percentages without GC logs showing a reason.
- No automatic `MaxGCPauseMillis=50`. It is a soft GC pause goal, not a guaranteed pause ceiling or a frame-time limit; a tighter goal can cost throughput.
- No fixed `Xms=Xmx` or `AlwaysPreTouch` by default. These can improve certain latency workloads but increase the initial memory floor or move memory preparation into startup.
- No forced GC thread counts, large-page requirements, experimental flags, or generic dedicated-server flag bundles.
- Do not select G1 and ZGC together. Preserve required vanilla, loader, and platform arguments.

Sources: [G1 tuning](https://docs.oracle.com/en/java/javase/21/gctuning/garbage-first-garbage-collector-tuning.html), [ZGC configuration and startup tradeoffs](https://inside.java/2023/11/28/gen-zgc-explainer/).

## Proposed launcher integration

1. Add two named presets and save the selected preset per user. Choose Efficient initially; suggest Smooth only with sufficient available memory and CPU headroom. RAM detection alone cannot prove ZGC will win.
2. Use the existing `MinimumRamMb` and `MaximumRamMb` fields for heap size. Put only the collector options in `ExtraJvmArguments`; do not duplicate `-Xms` or `-Xmx` there.
3. Normalize conflicting collector flags across the final generated command. Offer a clear return to Efficient if Smooth cannot initialize. Retain advanced custom arguments, validating conflicts before launch.
4. Apply these proposals to Islands. Determine the supported runtime separately for future editions.
5. Treat optional graphics/DH tuning as a separate decision. A JVM selection alone cannot correct GPU limits, excessive chunk work, or paging.

## Validation before calling a preset optimal

- Compare G1 and Generational ZGC at the **same maximum heap** first, so collector choice is not confused with a RAM increase.
- Use the same pack, Java patch, scene, server conditions, resolution, render distance, shader configuration, and DH state. Test DH off and on as separate cases.
- Repeat startup-to-menu and startup-to-play measurements. Distinguish a cold run from cached subsequent runs.
- Repeat a fixed gameplay route with chunk travel and a representative Create build. Measure frame-time percentiles and 1% lows as well as average FPS.
- Capture temporary GC logs, such as `-Xlog:gc*:gc-%p.log`, and examine pauses, collection frequency, heap occupancy, and ZGC allocation stalls. Measure total process memory and OS paging alongside CPU use.
- Validate low-end on a real constrained machine. Do not infer it from a high-end PC with a smaller heap alone.

The current evidence supports testing these two candidates. It does not yet establish a faster launch time, an FPS gain, or a universally best collector for DUSMP.
