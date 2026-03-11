# web-ui

## Purpose
Defines the Vue 3 + TypeScript web interface for browsing repository snapshots, initiating restores with cost estimates, viewing real-time operation progress, diffing snapshots, and displaying repository statistics.

## Requirements

### Requirement: File Explorer interface
The web UI SHALL provide a File Explorer-style interface for browsing repository snapshots, with a sidebar for snapshot selection and a main panel for directory browsing.

#### Scenario: Browse snapshot
- **WHEN** a user selects a snapshot from the sidebar
- **THEN** the main panel displays the root directory listing with file/folder icons, names, sizes, and modification dates

#### Scenario: Navigate directories
- **WHEN** a user clicks on a folder in the main panel
- **THEN** the panel navigates into that directory, showing its contents with a breadcrumb trail for navigation

### Requirement: Snapshot sidebar
The web UI SHALL display a sidebar listing all snapshots, grouped by date (year/month/day), with filtering by host and tags.

#### Scenario: Snapshot timeline
- **WHEN** the web UI loads
- **THEN** the sidebar shows snapshots grouped chronologically with collapsible year/month/day sections

#### Scenario: Filter by tag
- **WHEN** user selects a tag filter in the sidebar
- **THEN** only snapshots with that tag are displayed

### Requirement: Restore from UI
The web UI SHALL allow selecting files/directories and initiating a restore with a cost estimate dialog before rehydration.

#### Scenario: Restore selection
- **WHEN** user selects files in the browser and clicks "Restore"
- **THEN** a dialog shows the estimated rehydration cost, bytes, time, and priority options

#### Scenario: Confirm and restore
- **WHEN** user confirms the restore dialog
- **THEN** the restore operation starts and progress is displayed in real-time via SignalR

### Requirement: Real-time progress
The web UI SHALL display real-time progress for all running operations (backup, restore, prune) using SignalR streaming.

#### Scenario: Restore progress display
- **WHEN** a restore is in progress
- **THEN** the UI shows live progress bars for rehydration (packs ready vs pending) and file restoration (files completed vs remaining)

#### Scenario: Backup progress display
- **WHEN** a backup is in progress
- **THEN** the UI shows live counters for files scanned, bytes processed, and bytes uploaded

### Requirement: Vue 3 + TypeScript
The web UI SHALL be built with Vue 3 and TypeScript using the Composition API.

#### Scenario: Type-safe components
- **WHEN** the web UI is built
- **THEN** all components use TypeScript with proper type annotations for API responses and SignalR messages

### Requirement: Diff view
The web UI SHALL support viewing differences between two snapshots in a side-by-side or unified view.

#### Scenario: Visual diff
- **WHEN** user selects two snapshots and clicks "Diff"
- **THEN** the UI shows added, removed, and modified files with visual indicators

### Requirement: Repository stats dashboard
The web UI SHALL display a dashboard with repository statistics: total size, dedup ratio, snapshot count, storage cost breakdown by tier.

#### Scenario: Stats dashboard
- **WHEN** user navigates to the dashboard view
- **THEN** the UI displays repository statistics and a breakdown of cold vs archive tier storage
