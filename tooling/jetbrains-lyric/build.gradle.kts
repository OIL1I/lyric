// The thin plugin: the .lyr file association, the bundled TextMate grammar, and a descriptor
// that starts lyrls. Everything language-aware happens in the server — a PSI implementation
// would be a second frontend with a permanent lag, and it is deliberately not built.

plugins {
    // 2.3 at minimum: the 2026.2 platform's classes carry Kotlin 2.4.0 metadata, and a compiler
    // reads one minor version ahead of its own.
    id("org.jetbrains.kotlin.jvm") version "2.3.0"
    id("org.jetbrains.intellij.platform") version "2.18.1"
}

group = "dev.lyric"
version = providers.gradleProperty("pluginVersion").get()

kotlin {
    jvmToolchain(21)
}

repositories {
    mavenCentral()

    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    // Compile against the stdlib, ship none of it: the IDE brings its own, and a second copy in
    // the plugin is a classloader conflict waiting for a version bump. The gradle.properties
    // switch keeps it out of the distribution; this line keeps the compiler fed.
    compileOnly(kotlin("stdlib"))

    intellijPlatform {
        // The LSP API lives in the commercial IDEs; the unified IntelliJ IDEA is the compile
        // target. The 2026.1 baseline (sinceBuild 261) is what makes rename, signature help,
        // folding and inlay hints reach the IDE — each entered the platform's LSP integration
        // between 2024.2 and 2026.1, and a 2023.2 baseline would have kept only diagnostics,
        // completion and the jump.
        intellijIdea("2026.2.0.1")

        // The TextMate machinery that renders the bundled grammar.
        bundledPlugin("org.jetbrains.plugins.textmate")
    }
}

intellijPlatform {
    pluginConfiguration {
        id = "dev.lyric.jetbrains"
        name = "Lyric"
        version = project.version.toString()

        ideaVersion {
            sinceBuild = "261"
        }
    }

    buildSearchableOptions = false
}

// The grammar is not copied into this repository twice: the VS Code extension owns it (and the
// test suite pins it against the lexer); this build takes it from there at packaging time.
tasks.processResources {
    from(layout.projectDirectory.dir("../vscode-lyric")) {
        include("syntaxes/**", "language-configuration.json")
        into("textmate")
    }
}
