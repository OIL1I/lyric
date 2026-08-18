package dev.lyric.jetbrains

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.options.Configurable
import com.intellij.openapi.ui.TextFieldWithBrowseButton
import com.intellij.ui.dsl.builder.AlignX
import com.intellij.ui.dsl.builder.panel
import javax.swing.JComponent

/**
 * One setting: the toolchain directory. Left empty, `lyrls` is expected on PATH — the ordinary
 * case for an installed toolchain, and the same default the VS Code extension has.
 */
@State(name = "LyricSettings", storages = [Storage("lyric.xml")])
class LyricSettings : PersistentStateComponent<LyricSettings.SettingsState> {
    class SettingsState {
        var toolchainDirectory: String = ""
    }

    private var current = SettingsState()

    override fun getState(): SettingsState = current

    override fun loadState(state: SettingsState) {
        current = state
    }

    companion object {
        val instance: LyricSettings
            get() = ApplicationManager.getApplication().getService(LyricSettings::class.java)
    }
}

/** Settings | Languages & Frameworks | Lyric. */
class LyricConfigurable : Configurable {
    private var field: TextFieldWithBrowseButton? = null

    override fun getDisplayName(): String = "Lyric"

    override fun createComponent(): JComponent {
        val browser = TextFieldWithBrowseButton()
        field = browser

        return panel {
            row("Toolchain directory:") {
                cell(browser)
                    .align(AlignX.FILL)
                    .comment(
                        "The directory holding the lyric tools; lyrls is started from there. " +
                            "Leave empty to find lyrls on PATH. A change takes effect for newly " +
                            "opened files or after a restart of the IDE."
                    )
            }
        }
    }

    override fun isModified(): Boolean =
        field?.text != LyricSettings.instance.state.toolchainDirectory

    override fun apply() {
        LyricSettings.instance.state.toolchainDirectory = field?.text ?: ""
    }

    override fun reset() {
        field?.text = LyricSettings.instance.state.toolchainDirectory
    }

    override fun disposeUIResources() {
        field = null
    }
}
