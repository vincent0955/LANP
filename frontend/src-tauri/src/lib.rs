use std::sync::Mutex;

use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::Manager;
use tauri_plugin_shell::process::{CommandChild, CommandEvent};
use tauri_plugin_shell::ShellExt;

/// Handle to the bundled backend process, kept so it can be killed on app exit.
struct ApiSidecar(Mutex<Option<CommandChild>>);

/// True if something already serves 127.0.0.1:5000 — e.g. a `dotnet run` dev
/// backend. The dev backend takes precedence; spawning the bundled one would
/// only fail to bind and exit.
fn backend_already_running() -> bool {
  use std::net::{SocketAddr, TcpStream};
  use std::time::Duration;
  let addr: SocketAddr = ([127, 0, 0, 1], 5000).into();
  TcpStream::connect_timeout(&addr, Duration::from_millis(300)).is_ok()
}

fn spawn_backend(app: &tauri::AppHandle) -> Result<(), Box<dyn std::error::Error>> {
  if backend_already_running() {
    log::info!("port 5000 already serving; not starting the bundled API");
    return Ok(());
  }

  // Run the API with the resource dir as cwd so it picks up the bundled
  // appsettings.json and runtime tarball (it also has compiled-in defaults
  // for every setting).
  let resource_dir = app.path().resource_dir()?;
  let (mut rx, child) = app
    .shell()
    .sidecar("gamedashboard-api")?
    .current_dir(resource_dir)
    .spawn()?;

  app.state::<ApiSidecar>().0.lock().unwrap().replace(child);

  // Drain the output channel so the child's pipes never fill up; surface
  // stderr, which is where ASP.NET startup failures land.
  tauri::async_runtime::spawn(async move {
    while let Some(event) = rx.recv().await {
      match event {
        CommandEvent::Stderr(line) => {
          log::warn!("api: {}", String::from_utf8_lossy(&line))
        }
        CommandEvent::Terminated(status) => {
          log::warn!("bundled API exited: {status:?}")
        }
        _ => {}
      }
    }
  });

  Ok(())
}

/// Asks the backend to gracefully stop every running game server (30s save
/// window each) and shut the bundled runtime VM down. Raw loopback HTTP so we
/// don't pull in an HTTP client crate for one localhost POST. Blocking by
/// design: called from the tray Exit handler right before the app exits, and
/// exiting before the world save finishes is exactly what it prevents.
fn shutdown_servers_and_runtime() {
  use std::io::{Read, Write};
  use std::net::{SocketAddr, TcpStream};
  use std::time::Duration;

  let addr: SocketAddr = ([127, 0, 0, 1], 5000).into();
  let Ok(mut stream) = TcpStream::connect_timeout(&addr, Duration::from_secs(2)) else {
    log::info!("backend not reachable during exit; skipping graceful shutdown");
    return;
  };

  // Worst case: several servers saving in parallel plus the VM teardown.
  let _ = stream.set_read_timeout(Some(Duration::from_secs(90)));
  let request = "POST /api/runtime/shutdown HTTP/1.1\r\n\
                 Host: 127.0.0.1:5000\r\n\
                 Content-Length: 0\r\n\
                 Connection: close\r\n\r\n";
  if stream.write_all(request.as_bytes()).is_ok() {
    let mut response = Vec::new();
    let _ = stream.read_to_end(&mut response);
    log::info!("graceful shutdown completed");
  }
}

fn show_main_window(app: &tauri::AppHandle) {
  if let Some(window) = app.get_webview_window("main") {
    let _ = window.show();
    let _ = window.unminimize();
    let _ = window.set_focus();
  }
}

fn setup_tray(app: &tauri::App) -> tauri::Result<()> {
  let open = MenuItem::with_id(app, "open", "Open LANP", true, None::<&str>)?;
  let exit = MenuItem::with_id(app, "exit", "Exit (stops servers)", true, None::<&str>)?;
  let menu = Menu::with_items(app, &[&open, &exit])?;

  TrayIconBuilder::with_id("main")
    .icon(app.default_window_icon().unwrap().clone())
    .tooltip("LANP — game servers")
    .menu(&menu)
    // Left-click opens the window; the menu stays on right-click.
    .show_menu_on_left_click(false)
    .on_tray_icon_event(|tray, event| {
      if let TrayIconEvent::Click {
        button: MouseButton::Left,
        button_state: MouseButtonState::Up,
        ..
      } = event
      {
        show_main_window(tray.app_handle());
      }
    })
    .on_menu_event(|app, event| match event.id.as_ref() {
      "open" => show_main_window(app),
      "exit" => {
        // Servers save their worlds before the VM goes down; only then exit.
        shutdown_servers_and_runtime();
        app.exit(0);
      }
      _ => {}
    })
    .build(app)?;

  Ok(())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
  tauri::Builder::default()
    .plugin(tauri_plugin_shell::init())
    .setup(|app| {
      if cfg!(debug_assertions) {
        app.handle().plugin(
          tauri_plugin_log::Builder::default()
            .level(log::LevelFilter::Info)
            .build(),
        )?;
      }
      app.manage(ApiSidecar(Mutex::new(None)));
      setup_tray(app)?;
      // Best-effort: the UI's health screen reports a missing backend, so a
      // failed spawn must not prevent the window from opening.
      if let Err(e) = spawn_backend(app.handle()) {
        log::warn!("could not start the bundled API: {e}");
      }
      Ok(())
    })
    // Closing the window hides to the tray — servers (and the runtime VM)
    // keep running so closing the dashboard never kicks players mid-game.
    // Actually quitting is the tray menu's explicit Exit.
    .on_window_event(|window, event| {
      if let tauri::WindowEvent::CloseRequested { api, .. } = event {
        api.prevent_close();
        let _ = window.hide();
      }
    })
    .build(tauri::generate_context!())
    .expect("error while building tauri application")
    .run(|app, event| {
      if let tauri::RunEvent::Exit = event {
        if let Some(sidecar) = app.try_state::<ApiSidecar>() {
          if let Some(child) = sidecar.0.lock().unwrap().take() {
            let _ = child.kill();
          }
        }
      }
    });
}
