# JH Control Universal 3.0 — app nativa (.NET MAUI)

Busca tu TV **sola** en el WiFi y te muestra su **nombre real** (ej. "TV Sala", "Samsung 7 Series").
Tocas el nombre y queda conectada. Si la TV cambia de IP, la app la vuelve a encontrar sola.

| Marca | Estado |
|---|---|
| Roku (TCL, Hisense, Onn, Philips Roku TV, Roku Stick) | ✅ |
| Samsung Smart TV 2016 en adelante | ✅ (acepta «Permitir» en la TV la 1.ª vez) |
| LG webOS | 🧪 beta (acepta el aviso en la TV) |
| Android TV / Google TV (Sony, TCL, Xiaomi, Philips, Hisense Android, Westinghouse Google TV, Chromecast con Google TV) | ✅ la 1.ª vez escribes el código que sale en la TV |
| Cualquier TV, aunque no sea Smart (Samsung, LG, Sony) | ✅ por infrarrojo, si tu teléfono tiene (Xiaomi, Redmi, Poco…) |

---

## OPCIÓN A — Sacar el APK sin instalar nada (GitHub lo compila por ti)

1. Entra a **github.com** → botón **New** (nuevo repositorio) → nombre: `jh-control` → **Create repository**.
2. En el repositorio toca **uploading an existing file**.
3. Descomprime el ZIP en tu PC y **arrastra TODO el contenido** (la carpeta `JHControl`, la carpeta `.github`, `LEEME.md`, `.gitignore`).
   - Si la carpeta `.github` no se sube (algunas PC la ocultan), créala a mano: **Add file → Create new file** y escribe como nombre
     `.github/workflows/compilar-apk.yml`, luego pega el contenido de ese archivo.
4. Toca **Commit changes**.
5. Ve a la pestaña **Actions** → verás «Compilar APK de JH Control» corriendo (tarda unos 8–12 minutos).
6. Cuando salga ✅ verde, entra a esa ejecución → abajo en **Artifacts** descarga **JHControl-APK** (es un .zip con el `.apk` dentro).
7. Pasa el `.apk` al teléfono → ábrelo → permite «instalar apps desconocidas» → **Instalar**.

> Desinstala primero la versión web anterior para no confundirte.

---

## OPCIÓN B — Compilar en Visual Studio (Windows)

1. Abre **Visual Studio Installer** → **Modificar** → marca **Desarrollo de interfaz de usuario de aplicaciones multiplataforma de .NET** (.NET MAUI) → **Modificar**.
2. Descomprime el ZIP, por ejemplo en `C:\Proyectos\JHControl`.
3. En Visual Studio: **Archivo → Abrir → Proyecto o solución…** → elige `JHControl\JHControl.csproj`.
4. Espera a que termine de restaurar paquetes (barra de abajo).
   - Si sale error de versión: abre el `.csproj` y cambia `net10.0-android` por `net9.0-android` (en las 3 líneas donde aparece).
5. Conecta tu teléfono por USB con **Depuración USB** activada
   (Ajustes → Acerca del teléfono → toca 7 veces «Número de compilación» → Opciones de desarrollador → Depuración USB).
6. Arriba, en el botón verde ▶, elige **Dispositivos Android locales → tu teléfono** y dale ▶. La app se instala sola.
7. Para sacar el APK: clic derecho al proyecto → **Publicar…** → **Ad hoc** → sigue el asistente.
   O en **Herramientas → Línea de comandos → Símbolo del sistema para desarrolladores**:
   ```
   cd C:\Proyectos\JHControl\JHControl
   dotnet publish -f net10.0-android -c Release -o salida
   ```
   El APK queda en `salida\com.jhoniber.jhcontrol-Signed.apk`.

---

## Primer uso

1. Teléfono y TV en el **mismo WiFi** y la TV **encendida**.
2. Abre JH Control → empieza a buscar sola (unos 12 segundos).
3. Toca el **nombre de tu TV** → listo.
   - **Samsung:** en la TV sale «¿Permitir JH Control?» → **Permitir**.
   - **LG:** acepta el aviso en la TV.
   - **Roku:** no pide nada. Si no responde: Configuración → Sistema → Configuración avanzada del sistema →
     Control por aplicaciones móviles → Acceso de red: **Predeterminado**.

## Si no aparece

- La red de invitados de algunos routers no deja que los equipos se vean entre sí: usa la red principal.
- Algunos routers tienen **«Aislamiento AP / Aislamiento de clientes»**: desactívalo.
- Siempre puedes usar «¿No aparece? Agregar con IP» al final de la lista.

## iPhone

Se compila desde una **Mac** con Xcode (el proyecto ya incluye iOS y el permiso de «red local»).
Para instalarlo en otros iPhone hace falta una cuenta de Apple Developer.
