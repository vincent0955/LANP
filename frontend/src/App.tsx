import { HashRouter, Route, Routes } from "react-router-dom";
import { AppShell } from "@/features/shell/AppShell";
import { DisconnectedGate } from "@/features/shell/DisconnectedGate";
import { StartupGate } from "@/features/shell/StartupGate";
import { ServersPage } from "@/features/servers/ServersPage";
import { ServerDetailPage } from "@/features/servers/detail/ServerDetailPage";
import { LibraryPage } from "@/features/library/LibraryPage";
import { SetupPage } from "@/features/setup/SetupPage";
import { SettingsPage } from "@/features/settings/SettingsPage";

export default function App() {
  return (
    <HashRouter>
      <DisconnectedGate>
        <StartupGate>
          <Routes>
            <Route element={<AppShell />}>
              <Route index element={<ServersPage />} />
              <Route path="servers/:name" element={<ServerDetailPage />} />
              <Route path="library" element={<LibraryPage />} />
              <Route path="setup" element={<SetupPage />} />
              <Route path="settings" element={<SettingsPage />} />
            </Route>
          </Routes>
        </StartupGate>
      </DisconnectedGate>
    </HashRouter>
  );
}
