# Remote Training, PWA Web App & Public Internet Serving

## Overview

LoRAMancer supports distributed remote training and cross-platform access across your local area network (LAN), over VPNs, or across the public internet:
1. **Custom Server & Reverse Proxy Setup**: Run LoRAMancer behind your own existing web server setup (Nginx, Caddy, Traefik, Apache, or direct port forwarding) with custom domain support. No third-party services required.
2. **Strict Auth Token Security**: When enabled, **every incoming connection without a valid auth token is strictly denied (HTTP 401 Unauthorized)**. Your GPU, file system, and training telemetry are completely shielded.
3. **Progressive Web App (PWA)**: Install the remote web frontend directly as a native-feel desktop or mobile application on Linux PCs, Macs, Chromebooks, iPads, and Android phones.
4. **App-to-App Client Mode**: Run LoRAMancer as a native desktop application on a secondary Windows laptop/PC and dispatch training jobs to the GPU on your AI PC.
5. **Live Streaming Telemetry**: Real-time progress updates, step counters, loss metrics, and terminal console logs stream to all connected clients over Server-Sent Events (SSE).

---

## 1. Host Mode Setup (AI PC)

On the computer with the training GPU (AMD ROCm, NVIDIA CUDA, Intel XPU):
1. Open **Settings → Remote Server & Worker**.
2. Enable **Enable Network Server & Web View**.
3. Configure the listening parameters:
   - **Port**: Default `8420` (or any custom internal port).
   - **Bind IP**: `0.0.0.0` (all network adapters) or `127.0.0.1` (local loopback only behind local reverse proxy).
   - **Auto-Configure Windows Firewall**: Click the **Auto-Configure Windows Firewall** button directly below the port input. This automatically elevates with a UAC prompt and executes `netsh advfirewall` to add an inbound rule (`LoRAMancer Web Server`) allowing TCP traffic on your configured port across all network profiles.
   - **Auth Token**: Enter your private secret token/key.
   - **Strict Auth (Deny Missing Token)**: When enabled, any request without this token is immediately rejected with HTTP 401.

---

## 2. Using Your Own Server Setup (WAN / Internet Access)

If you already have your own server infrastructure, reverse proxy, or public IP:

### Reverse Proxy Configuration (Nginx / Caddy / Traefik)
Simply proxy traffic from your domain to LoRAMancer's listening port (e.g. `127.0.0.1:8420` or LAN IP).

#### Example Caddyfile:
```caddy
lora.yourdomain.com {
    reverse_proxy 127.0.0.1:8420
}
```

#### Example Nginx:
```nginx
server {
    server_name lora.yourdomain.com;

    location / {
        proxy_pass http://127.0.0.1:8420;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

In LoRAMancer **Settings → Remote Server & Worker**, enter your domain in **Custom Server / Domain URL** (e.g. `https://lora.yourdomain.com`). The interface generates quick-connect links with one-click clipboard copying.

*(Note: Cloudflare Quick Tunnels remain available as an optional fallback for users without their own server or domain).*

---

## 3. Strict Token Enforcement & Connecting

When **Strict Auth** is enabled:
- **Zero Token = Strict 401 Denial**: Any unauthorized connection to `/`, `/api/*`, `/manifest.json`, or `/sw.js` returns `401 Unauthorized`. Unauthenticated probes or internet port scanners learn nothing about your GPU or training jobs.
- **Accessing via Browser / Web App**:
  - **Option A (One-Click URL)**: Navigate to `https://lora.yourdomain.com/?token=YOUR_AUTH_TOKEN`. The server validates the token, writes a secure session cookie, and loads the full training interface.
  - **Option B (Authentication Gate)**: Navigate to `https://lora.yourdomain.com/`. You will be greeted by a secure 401 Lock Screen prompting for your Auth Token. Entering the token authenticates your session and reloads the app.
- **REST & SSE Connections**: Pass the token via `Authorization: Bearer <token>`, `?token=<token>`, or the `loramancer_auth` session cookie.

---

## 4. Installing as a Web App (PWA)

The remote interface is a fully compliant Progressive Web App (PWA):
- **Web App Manifest (`/manifest.json`)**: Declares application name, standalone display mode, dark theme `#11111b`, accent `#cba6f7`, and high-resolution icons.
- **Service Worker (`/sw.js`)**: Caches the application shell assets (`/`, `/manifest.json`, `/icon.svg`) for rapid launching and resilience against intermittent mobile connections.
- **Standalone Window**: Runs without browser address bars or navigation clutter, matching the look and feel of a native desktop application.

### Installation Instructions:
- **Chrome / Edge / Brave (Desktop & Laptop)**: Open the URL. Click the **📲 Install App** button in the header, or click the Install icon in the browser address bar.
- **macOS (Safari)**: Open the URL in Safari, click **File → Add to Dock**, and launch LoRAMancer as a standalone Mac application.
- **Android**: Open the URL in Chrome, tap the **Install App** button or the three-dot menu and select **Install app** / **Add to Home screen**.
- **iOS (iPhone & iPad)**: Open the URL in Safari, tap the **Share** button, and select **Add to Home Screen**.

---

## 5. Windows Desktop Client Mode (Laptop / Secondary PC)

If you are on another Windows PC and prefer using the native Windows desktop client:
1. Install and launch LoRAMancer on your laptop (no GPU or PyTorch installation required).
2. Navigate to **Settings → Remote Server & Worker**.
3. Under **Remote Client Mode**:
   - Toggle **Connect to Remote AI PC**.
   - Enter your server address (e.g. `https://lora.yourdomain.com` or `http://192.168.1.150:8420`).
   - Enter your **Auth Token**.
   - Click **Test Connection** to verify host availability, OS, and GPU status.
4. Go to **Training Console**:
   - A blue indicator banner confirms **Remote Engine Mode Active**.
   - Training runs will dispatch configs and dataset archives across the network and stream live progress.

---

## 6. API Endpoints

The embedded Kestrel server exposes the following endpoints (all protected by Auth Token when enabled):

| Endpoint | Method | Description |
|---|---|---|
| `/` | `GET` | Responsive embedded web control interface and PWA app shell (returns 401 Lock Gate if unauthorized) |
| `/manifest.json` | `GET` | Web App Manifest providing PWA metadata, standalone display mode, and icons |
| `/icon.svg` | `GET` | Vector SVG application icon with maskable support |
| `/sw.js` | `GET` | Progressive Web App Service Worker for shell asset caching |
| `/api/v1/auth/login` | `POST` | Authenticates Auth Token, setting secure session cookie |
| `/api/v1/health` | `GET` | Health probe returning host machine name, GPU info, and training state |
| `/api/v1/datasets/upload` | `POST` | Multipart form upload of dataset `.zip` archive, auto-extracted on host |
| `/api/v1/training/start` | `POST` | Dispatches and initiates a training job with config YAML |
| `/api/v1/training/stop` | `POST` | Sends a cancellation signal to the active training runner |
| `/api/v1/training/stream` | `GET` | Server-Sent Events (SSE) streaming live step telemetry, loss metrics, and logs |
| `/api/v1/history` | `GET` | List all past training runs in the Vault with status, metrics, and timestamps |
| `/api/v1/history/retry/{id}` | `POST` | Immediately resubmit and start a training run from history on the host |
| `/api/v1/tokens` | `GET` | List all auth tokens with status, role, usage, and plain-text secret |
| `/api/v1/tokens` | `POST` | Create a new auth token with custom or auto-generated `lrm_...` secret |
| `/api/v1/tokens/{id}/block` | `POST` | Immediately block an auth token, revoking server access |
| `/api/v1/tokens/{id}/unblock` | `POST` | Unblock a previously revoked auth token |
| `/api/v1/tokens/{id}` | `DELETE` | Permanently remove an auth token from the system |

---

## 7. Core Auth Token Manager

LoRAMancer includes a built-in Core Auth Token Manager (`AuthTokenManagerService`) accessible under **Settings → Auth Tokens**:
- **Plain-Text Visibility for Administrators**: Auth token secrets remain visible in plain text with one-click clipboard copying. No hiding with asterisks or irreversible redaction.
- **Role-Based Access Control**:
  - `Admin`: Full server control, token management, and configuration access.
  - `Trainer`: Start/stop training runs, upload datasets, and stream logs.
  - `ReadOnly`: Monitor live training loss and inspect telemetry without mutation permissions.
- **Lifecycle & Auditing**:
  - **Instant Revocation**: Block compromised or temporary tokens with 1 click without deleting their audit record.
  - **Usage Telemetry**: Tracks creation timestamps, expiration dates, last used timestamp, and total authentication hits.
  - **Cryptographic Generation**: Issues cryptographically secure `lrm_<48-hex>` tokens or accepts custom strings.

