Architectural Diagram
     
     +-------------------+
     |       D365        |
     +--------+----------+
              |
              | HTTP POST (JSON payload)
              | Auth: Shared Secret
              v
     +--------+----------+
     | Azure Function 1  | <- HTTP Trigger: /ReceiveAndQueue
     |  "ReceivePayload" |    (Gated by Control number)
     +--------+----------+
              |
              | Queue message (includes JSON, metadata)
              v
     +--------+----------+
     | Azure Queue       | <- "dispatch-processing-queue" (in built retries)
     +--------+----------+
              |
              v
     +--------+----------+
     | Azure Function 2  | <- Queue Trigger
     | "TransformAndSend"|
     +--------+----------+
              |
      +-------+--------+       +---------------------+
      | Transform JSON  |----->|  Map fields         |
      | Validate schema |      |  Create CSV         |
      +-----------------+      +---------------------+
              |
              v
     +--------+----------+
     | Upload to SFTP    | <- SFTP with PKI Auth (via static IP)
     +--------+----------+
              |
              v
     +-------------------+
     | Azure Monitor /    |
     | App Insights       | <- Logs, alerts, telemetry
     +-------------------+

Still to implement:
  - Store and read control number to prevent duplicate records in queue
  - retries for file upload
  - Static Ip (via virtual NAT Gateway) for white listing
  - File size hard restriction
  - Unit tests/integration testing with sample payload and expected results

Possible Future Enhancements:
  - Terraform to generate and regenerate all azure resources
  - Optimize trigger mechanisms and time intervals (cost benefits)
  - Different environments dev qa prod setup on github
  - Alternative way to store schemas and mappings to allow easier switch/versioning support
  - Email notifications to update progress
  - storage of records (maybe periodic clean up)
  - Ui/Admin page to view and download stored records


