# Remote Training & Web UI Architecture

## Overview

LoRAMancer supports distributed remote training and cross-platform access across your local area network (LAN) or over VPNs (such as Tailscale):
1. **Browser Web UI (Zero Install)**: Access the full LoRAMancer interface directly from Chrome/Firefox on Linux PCs, Macs, iPads, or smartphones with zero installation.
2. **App-to-App Client Mode**: Run LoRAMancer as a native desktop application on a secondary Windows laptop/PC and dispatch training jobs to the GPU on your AI PC.
3. **Live Streaming Telemetry**: Real-time progress updates, step counters, loss metrics, and terminal console logs stream to all connected clients over Server-Sent Events (SSE).

---

## 1. Host Mode (AI PC)

On the computer with the training GPU (NVIDIA CUDA or AMD ROCm):
1. Navigate to **Settings → Remote Server & Worker**.
2. Enable **Host Network Server & Web View**.
3. Configure the listening parameters:
   - **Port**: Default `8420` (or any custom open port).
   - **Bind IP**: `0.0.0.0` (listens on all network adapters for LAN access) or `127.0.0.1` (local loopback only).
   - **Access PIN / Token (Optional)**: Set an authentication token to restrict network access.
4. Click **Start Server Now**.

The embedded Kestrel server will start immediately and display the listening URL (e.g. `http://192.168.1.150:8420`).

---

## 2. Browser Web View (Linux, Mac, Tablet)

From any computer or mobile device on the network:
1. Open any web browser (Chrome, Firefox, Safari, Edge).
2. Navigate to `http://<ai-pc-ip>:8420` (e.g. `http://192.168.1.150:8420`).
3. The embedded web application provides:
   - **Run Configuration**: Select base architecture (FLUX.1, SDXL, etc.), trigger word, and training steps.
   - **Dataset ZIP Upload**: Upload a `.zip` archive of images/captions directly from your browser. The AI PC automatically extracts and validates it.
   - **Real-Time Terminal**: Live stdout/stderr log output directly from the host's training runner.
   - **Live Progress & Loss Metrics**: Step progress bar, active loss readout, and client connection counts.

---

## 3. Windows Desktop Client Mode (Laptop / Secondary PC)

If you are on another Windows PC and want to use the native desktop application:
1. Install and launch LoRAMancer on your laptop (no GPU or PyTorch installation required).
2. Navigate to **Settings → Remote Server & Worker**.
3. Under **Remote Client Mode**:
   - Toggle **Connect to Remote AI PC**.
   - Enter your AI PC's address: `http://<ai-pc-ip>:8420`.
   - Enter your Access PIN / Token (if configured on the host).
   - Click **Test Connection** to verify host availability, OS, and GPU status.
4. Go to **Training Console**:
   - Notice the blue banner indicating **Remote Engine Mode Active**.
   - Launching a training run will automatically transmit the YAML configuration and stream real-time progress and logs to your desktop console.

---

## 4. API Endpoints

The embedded Kestrel server exposes the following REST and SSE endpoints:

| Endpoint | Method | Description |
|---|---|---|
| `/api/v1/health` | `GET` | Health probe returning host machine name, GPU info, and training state |
| `/api/v1/datasets/upload` | `POST` | Multipart form upload of dataset `.zip` archive, auto-extracted to `~/.loramancer/extracted_datasets/` |
| `/api/v1/training/start` | `POST` | Dispatches and initiates a training job with config YAML |
| `/api/v1/training/stop` | `POST` | Sends a cancellation signal to the active training runner |
| `/api/v1/training/stream` | `GET` | Server-Sent Events (SSE) streaming live step telemetry, loss metrics, and stdout logs |
| `/` | `GET` | Serves the responsive web control interface for browsers |
