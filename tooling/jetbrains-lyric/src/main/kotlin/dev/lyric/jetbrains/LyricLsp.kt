package dev.lyric.jetbrains

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import java.nio.file.Files
import java.nio.file.Path

/**
 * The plugin's whole language intelligence: recognise a `.lyr` file and start `lyrls` for it.
 *
 * Everything after that — diagnostics, completion, the jump, references, rename, semantic
 * tokens, signature help, folding, inlay hints — is the platform consuming the same server the
 * VS Code extension talks to. One server, two editors, no second frontend.
 *
 * The DEPRECATED API names are used on purpose: `LspServerSupportProvider` is documented as
 * preserved and fully functional, while its successor exists only from 2026.1.4 — pinning the
 * plugin to a point release for a rename of the same API would be baseline for nothing.
 */
class LyricLspServerSupportProvider : LspServerSupportProvider {
    override fun fileOpened(
        project: Project,
        file: VirtualFile,
        serverStarter: LspServerSupportProvider.LspServerStarter,
    ) {
        if (file.extension == "lyr") {
            serverStarter.ensureServerStarted(LyricLspServerDescriptor(project))
        }
    }
}

class LyricLspServerDescriptor(project: Project) : ProjectWideLspServerDescriptor(project, "Lyric") {
    override fun isSupportedFile(file: VirtualFile): Boolean = file.extension == "lyr"

    override fun createCommandLine(): GeneralCommandLine =
        GeneralCommandLine(LyricToolchain.languageServer())
}

/**
 * Where `lyrls` lives — the same ladder the VS Code extension climbs: an explicit setting first,
 * then the bare name, which leaves the lookup to PATH. The setting names the toolchain DIRECTORY
 * rather than the binary, because the directory is what an installation is.
 */
object LyricToolchain {
    fun languageServer(): String {
        val configured = LyricSettings.instance.state.toolchainDirectory.trim()
        if (configured.isNotEmpty()) {
            val binary = if (System.getProperty("os.name").startsWith("Windows")) {
                "lyrls.exe"
            } else {
                "lyrls"
            }

            val candidate = Path.of(configured).resolve(binary)
            if (Files.isRegularFile(candidate)) return candidate.toString()
        }

        return "lyrls"
    }
}
