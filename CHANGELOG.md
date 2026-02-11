# Changelog

## v1.0.0 — Initial Release

### Full Edition (Dashboard + Server-Side Collection)

- **30+ automated collectors** via SQL Agent jobs: wait stats, CPU, memory, query performance, index usage, file I/O, blocking, deadlocks, and more
- **Dashboard application** (WPF/.NET 8) with real-time charts and trend analysis
- **CLI and GUI installers** for automated server-side setup
- **SQL Server 2016-2025** support including Azure SQL DB, Azure Managed Instance, and AWS RDS
- **Email alerts** for blocking, deadlocks, and high CPU
- **MCP server** (experimental) for LLM tool integration
- **Data retention** with configurable automatic cleanup
- **Delta normalization** for per-second rate calculations across all trend charts

### Lite Edition (No Server Installation Required)

- **Agentless monitoring** — connects directly to SQL Server DMVs, no installation on monitored servers
- **Local DuckDB storage** for historical data and trend analysis
- **System tray operation** with background collection and alert notifications
- **All chart and analysis features** from the Full Dashboard
- **MCP server** (experimental) for LLM tool integration

### Supported Platforms

- SQL Server 2016, 2017, 2019, 2022, 2025
- Azure SQL Database
- Azure SQL Managed Instance
- AWS RDS for SQL Server
