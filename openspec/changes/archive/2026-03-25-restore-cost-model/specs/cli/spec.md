## MODIFIED Requirements

### Requirement: Restore cost confirmation display
The CLI SHALL display a per-component cost estimate table before restoring and require interactive confirmation. The table SHALL show cost components as rows (Data retrieval, Read operations, Write operations, Storage) with columns for Standard and High Priority. A total row SHALL summarize each column. The storage row SHALL indicate the assumed duration (e.g., "Storage (1 month)"). The display SHALL also include: files to restore, chunks categorized by availability (cached, Hot/Cool, already rehydrated, needs rehydration, pending rehydration), total compressed size.

#### Scenario: Cost confirmation table display
- **WHEN** a restore requires 200 archive-tier chunks totaling 20 GB
- **THEN** the CLI SHALL display a Spectre.Console table with 4 cost component rows, Standard and High Priority columns, and a total row

#### Scenario: Currency displayed from config
- **WHEN** the pricing config uses EUR rates
- **THEN** the cost values in the table SHALL be formatted with the EUR currency symbol

#### Scenario: Storage assumption visible
- **WHEN** the cost table is displayed
- **THEN** the storage row SHALL be labeled "Storage (1 month)" to indicate the assumed duration
