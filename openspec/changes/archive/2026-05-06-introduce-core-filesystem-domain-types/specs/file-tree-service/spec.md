## ADDED Requirements

### Requirement: Relative path filetree staging
Filetree staging SHALL consume validated relative domain paths when adding file entries and SHALL derive filetree entry names and parent directories from those paths.

#### Scenario: Stage file entry from relative path
- **WHEN** filetree staging appends a file entry for `photos/2024/pic.jpg`
- **THEN** it SHALL stage the file entry named `pic.jpg` under the relative directory `photos/2024`

#### Scenario: Reject invalid staged path
- **WHEN** filetree staging is asked to append a file entry for `photos/../pic.jpg`
- **THEN** it SHALL reject the path before writing staging data

### Requirement: Canonical filetree entry names
Filetree entries SHALL use validated path-segment names internally. Directory formatting for persisted filetree lines MAY differ from previous formats when the new representation is clearer, because backward compatibility is not required for this change.

#### Scenario: File entry name is one segment
- **WHEN** a filetree file entry is created for `photos/pic.jpg`
- **THEN** the entry name SHALL be `pic.jpg` and SHALL NOT include `/`

#### Scenario: Directory entry name is one segment
- **WHEN** a filetree directory entry is created for `photos`
- **THEN** the entry name SHALL represent one validated segment and SHALL NOT contain path traversal segments

### Requirement: Filetree traversal uses relative paths
Filetree traversal for archive, list, and restore SHALL build child paths through relative path composition rather than string interpolation.

#### Scenario: Traverse child directory
- **WHEN** traversal descends from `photos` into child segment `2024`
- **THEN** the child relative path SHALL be composed as `photos/2024` through the relative path model

#### Scenario: Traverse child file
- **WHEN** traversal visits file segment `pic.jpg` under directory `photos/2024`
- **THEN** the file relative path SHALL be composed as `photos/2024/pic.jpg` through the relative path model
