import baseline.HitProbe

fun main(args: Array<String>) {
    val forwarded = args.drop(1).toTypedArray()
    when (val harness = args.firstOrNull()) {
        "caffeine-hit" -> HitProbe.run(HitProbe.Engine.CAFFEINE, forwarded)

        "guava-hit" -> HitProbe.run(HitProbe.Engine.GUAVA, forwarded)

        "caffeine-scenarios" -> ScenarioProbe.main(forwarded)

        "caffeine-write" -> org.openjdk.jmh.Main.main(forwarded)

        null,
        "--help",
        "-h",
        -> println("Usage: jvm-benchmarks <caffeine-hit|guava-hit|caffeine-scenarios|caffeine-write> [args]")

        else -> throw IllegalArgumentException("Unknown harness: $harness")
    }
}
