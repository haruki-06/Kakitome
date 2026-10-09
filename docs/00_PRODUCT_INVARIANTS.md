# 00 — Product Invariants

## Product

Kakitome is a new Windows-native application focused on lectures, meetings, interviews, and personal voice notes. It also accepts supported local audio/video files and supported media URLs.

## Core principles

### 1. Zero recurring cost by default

The core application must not require:

- a server;
- an account;
- a subscription;
- a paid API;
- a cloud AI provider.

The first-release AI path is local/on-device.

### 2. Laptop-first

The application is designed around Windows laptops, not high-end desktops:

- battery life matters;
- thermals matter;
- RAM and storage may be constrained;
- foreground responsiveness outranks background throughput;
- NPU/GPU are optional accelerators, not prerequisites.

### 3. Offline-first

After required local models are downloaded, recording, transcription, cleanup, summarization, search, and export should work without Internet access.

### 4. Portable user data

Library files must remain useful without Kakitome. The application is a productivity tool around user-owned files, not a database lock-in product.

### 5. No silent destruction

Kakitome must never silently delete a recording, transcript, summary, project, backup, or other canonical Library data to recover from an error or free storage.

### 6. Replaceable AI

ASR, decision, summary, and embedding providers are replaceable capabilities. Default models are selected by benchmark, not hard-coded preference.

### 7. Native Windows UX

Use WinUI 3 and Windows APIs when they materially improve the product. Do not recreate the legacy macOS UI or use a web framework as the primary UI.
