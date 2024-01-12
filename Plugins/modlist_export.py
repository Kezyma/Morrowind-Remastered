try:
    from PyQt5.QtCore import QCoreApplication, QStandardPaths
    from PyQt5 import QtCore, QtWidgets
    from PyQt5.QtWidgets import QDialog, QWidget, QVBoxLayout, QHBoxLayout, QSizePolicy, QLabel, QFileDialog
    from PyQt5.QtGui import QIcon, QFont
    qItemFlag = QtCore.Qt
    qSizePolicy = QtWidgets.QSizePolicy
    qAlignmentFlag = QtCore.Qt
    qCheckState = QtCore.Qt
    qDialogCode = QtWidgets.QDialog
    qStandardPaths = QStandardPaths
except:
    from PyQt6.QtCore import QCoreApplication, QStandardPaths
    from PyQt6 import QtCore, QtWidgets
    from PyQt6.QtWidgets import QDialog, QWidget, QVBoxLayout, QHBoxLayout, QSizePolicy, QLabel, QFileDialog
    from PyQt6.QtGui import QIcon, QFont
    qSizePolicy = QtWidgets.QSizePolicy.Policy
    qAlignmentFlag = QtCore.Qt.AlignmentFlag
    qDialogCode = QtWidgets.QDialog.DialogCode
    qItemFlag = QtCore.Qt.ItemFlag
    qCheckState = QtCore.Qt.CheckState
    qStandardPaths = QStandardPaths.StandardLocation

try:
    from PyQt5.QtCore import QCoreApplication, QStandardPaths
    from PyQt5.QtWidgets import QFileDialog
    qDocLocation = QStandardPaths.DocumentsLocation
except:
    from PyQt6.QtCore import QCoreApplication, QStandardPaths
    from PyQt6.QtWidgets import QFileDialog
    qDocLocation = QStandardPaths.StandardLocation.DocumentsLocation

try:
    from PyQt5.QtCore import qInfo, qDebug, qWarning, qCritical
except:
    from PyQt6.QtCore import qInfo, qDebug, qWarning, qCritical

import mobase, os, json
from pathlib import Path

class ModlistExporter(mobase.IPluginTool):
    """ Base class for all plugins to inherit from and overwrite. """

    def __init__(self):
        self._pluginName = "ModlistExporter"
        self._displayName = "Modlist Exporter"
        self._pluginVersion = mobase.VersionInfo(1, 0, 0, mobase.ReleaseType.FINAL)
        super().__init__()

    def init(self, organiser = mobase.IOrganizer):
        self._organiser = organiser
        return True

    def __tr(self, trstr):
        return QCoreApplication.translate(self._pluginName, trstr)
        
    def isActive(self):
        return self._baseSettings.enabled()

    def __tr(self, trstr):
        return QCoreApplication.translate(self._pluginName, trstr)

    def tr(self, trstr):
        return self.__tr(trstr)

    def author(self):
        return "Kezyma"

    def baseName(self):
        return self._pluginName

    def baseDisplayName(self):
        return self._displayName
    
    def icon(self):
        return QIcon()

    def name(self):
        return self.baseName()

    def displayName(self):
        return self.baseDisplayName()

    def description(self):
        return self.__tr("Exports the current modlist as json.")

    def tooltip(self):
        return self.description()

    def settings(self):
        """ Current list of game settings for Mod Organizer. """
        return [
            mobase.PluginSetting("enabled", f"Enables {self._pluginName}", True),
            mobase.PluginSetting("loglevel", f"Controls the logging for {self._pluginName}", 1)
            ]

    def display(self):
        manualPath = Path(QFileDialog.getSaveFileName(None, "Please select where modlist data should be exported.", ".", "JSON Files (*.json)")[0])
        modList = self._organiser.modList()
        orderedMods = modList.allModsByProfilePriority()
        modExport = {}
        currentCat = ""
        for mod in orderedMods:
            qInfo(str(mod))
            if str(mod).endswith("_separator"):
                currentCat = str(mod).replace("_separator", "")
                modExport[currentCat] = []
            else:
                if modList.state(mod.encode('utf-16','surrogatepass').decode('utf-16')) & mobase.ModState.ACTIVE:
                    modItem = modList.getMod(mod)
                    nexusGame = self._organiser.getGame(modItem.gameName()).gameNexusName()
                    newMod = {
                        "Name": modItem.name(),
                        "Game": modItem.gameName(),
                        "NexusId": modItem.nexusId(),
                        "NexusGame": nexusGame,
                        "Comments": modItem.comments(),
                        "Notes": modItem.notes()
                    }
                    modExport[currentCat].append(newMod)

        os.makedirs(os.path.dirname(manualPath), exist_ok=True)
        with open(Path(manualPath), "w", encoding="utf-8") as jsonFile:
            json.dump(modExport, jsonFile)


def createPlugins():
    plugins = [ModlistExporter()]
    return plugins
