use std::sync::Mutex;

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
  // appsettings.json (it also has compiled-in defaults for every setting).
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
      // Best-effort: the UI's health screen reports a missing backend, so a
      // failed spawn must not prevent the window from opening.
      if let Err(e) = spawn_backend(app.handle()) {
        log::warn!("could not start the bundled API: {e}");
      }
      Ok(())
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
