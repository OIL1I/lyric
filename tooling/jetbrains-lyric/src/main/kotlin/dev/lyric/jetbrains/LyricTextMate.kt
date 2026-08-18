package dev.lyric.jetbrains

import java.nio.file.Files
import java.nio.file.Path
import org.jetbrains.plugins.textmate.api.TextMateBundleProvider

/**
 * Hands the IDE the same TextMate grammar the VS Code extension ships — the one the test suite
 * pins against the lexer, taken from there at build time rather than copied into a second life.
 *
 * The TextMate machinery reads bundles from the file system, and this plugin's resources live in
 * a jar, so they are extracted once per version into the plugin's cache-shaped corner of the
 * system temp directory. A VS Code-style bundle is a directory with a `package.json`; the one
 * written here declares only what the grammar needs.
 */
class LyricTextMateBundleProvider : TextMateBundleProvider {
    override fun getBundles(): List<TextMateBundleProvider.PluginBundle> {
        val bundle = extracted() ?: return emptyList()
        return listOf(TextMateBundleProvider.PluginBundle("Lyric", bundle))
    }

    private fun extracted(): Path? {
        val target = Path.of(
            System.getProperty("java.io.tmpdir"),
            "lyric-textmate-${javaClass.`package`?.implementationVersion ?: "dev"}"
        )

        val files = mapOf(
            "textmate/syntaxes/lyric.tmLanguage.json" to "syntaxes/lyric.tmLanguage.json",
            "textmate/language-configuration.json" to "language-configuration.json",
        )

        for ((resource, relative) in files) {
            val stream = javaClass.classLoader.getResourceAsStream(resource) ?: return null
            val destination = target.resolve(relative)
            Files.createDirectories(destination.parent)
            stream.use { Files.copy(it, destination, java.nio.file.StandardCopyOption.REPLACE_EXISTING) }
        }

        Files.writeString(target.resolve("package.json"), MANIFEST)
        return target
    }

    private companion object {
        /** The VS Code bundle shape, reduced to the members the TextMate reader consumes. */
        const val MANIFEST = """{
  "name": "lyric",
  "version": "0.0.0",
  "contributes": {
    "languages": [
      {
        "id": "lyric",
        "extensions": [".lyr"],
        "configuration": "./language-configuration.json"
      }
    ],
    "grammars": [
      {
        "language": "lyric",
        "scopeName": "source.lyric",
        "path": "./syntaxes/lyric.tmLanguage.json"
      }
    ]
  }
}
"""
    }
}
