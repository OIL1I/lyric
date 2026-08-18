rootProject.name = "jetbrains-lyric"

plugins {
    // Downloads the JDK the toolchain block names when the machine has none — the CI runner and
    // a fresh clone both build without a hand-installed Java 21.
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}
