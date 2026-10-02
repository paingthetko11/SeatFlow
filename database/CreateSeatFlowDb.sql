/*
    SeatFlow database schema for SQL Server.
    Run in SSMS as a login that can create databases.
    Redis is used for short-lived distributed holds; ShowSeats is the
    SQL Server source of truth and provides a second concurrency safeguard.
*/

IF DB_ID(N'SeatFlowDb') IS NULL
    CREATE DATABASE [SeatFlowDb];
GO

USE [SeatFlowDb];
GO

CREATE TABLE dbo.Venues
(
    VenueId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Venues PRIMARY KEY,
    Name          NVARCHAR(200) NOT NULL,
    Address       NVARCHAR(500) NULL,
    CreatedAtUtc  DATETIME2(3) NOT NULL CONSTRAINT DF_Venues_CreatedAtUtc DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.Events
(
    EventId       INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Events PRIMARY KEY,
    Name          NVARCHAR(200) NOT NULL,
    Description   NVARCHAR(2000) NULL,
    Category      NVARCHAR(100) NULL,
    CreatedAtUtc  DATETIME2(3) NOT NULL CONSTRAINT DF_Events_CreatedAtUtc DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.Seats
(
    SeatId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Seats PRIMARY KEY,
    VenueId       INT NOT NULL,
    Section       NVARCHAR(50) NOT NULL,
    RowLabel      NVARCHAR(10) NOT NULL,
    SeatNumber    INT NOT NULL,
    CreatedAtUtc  DATETIME2(3) NOT NULL CONSTRAINT DF_Seats_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Seats_Venues FOREIGN KEY (VenueId) REFERENCES dbo.Venues(VenueId),
    CONSTRAINT UQ_Seats_Venue_Section_Row_Number UNIQUE (VenueId, Section, RowLabel, SeatNumber)
);
GO

CREATE TABLE dbo.Shows
(
    ShowId        INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Shows PRIMARY KEY,
    EventId       INT NOT NULL,
    VenueId       INT NOT NULL,
    StartsAtUtc   DATETIME2(3) NOT NULL,
    EndsAtUtc     DATETIME2(3) NULL,
    Status        NVARCHAR(20) NOT NULL CONSTRAINT DF_Shows_Status DEFAULT N'Scheduled',
    CreatedAtUtc  DATETIME2(3) NOT NULL CONSTRAINT DF_Shows_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Shows_Events FOREIGN KEY (EventId) REFERENCES dbo.Events(EventId),
    CONSTRAINT FK_Shows_Venues FOREIGN KEY (VenueId) REFERENCES dbo.Venues(VenueId),
    CONSTRAINT CK_Shows_Status CHECK (Status IN (N'Scheduled', N'OnSale', N'Cancelled', N'Completed')),
    CONSTRAINT CK_Shows_EndAfterStart CHECK (EndsAtUtc IS NULL OR EndsAtUtc > StartsAtUtc)
);
GO

/* One inventory row per seat per show. Conditional updates against this row
   prevent two requests from successfully claiming the same seat in SQL Server. */
CREATE TABLE dbo.ShowSeats
(
    ShowId          INT NOT NULL,
    SeatId          INT NOT NULL,
    Price           DECIMAL(10,2) NOT NULL,
    Status          NVARCHAR(20) NOT NULL CONSTRAINT DF_ShowSeats_Status DEFAULT N'Available',
    HoldToken       UNIQUEIDENTIFIER NULL,
    HoldExpiresAtUtc DATETIME2(3) NULL,
    BookingId       UNIQUEIDENTIFIER NULL,
    UpdatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_ShowSeats_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_ShowSeats PRIMARY KEY (ShowId, SeatId),
    CONSTRAINT FK_ShowSeats_Shows FOREIGN KEY (ShowId) REFERENCES dbo.Shows(ShowId),
    CONSTRAINT FK_ShowSeats_Seats FOREIGN KEY (SeatId) REFERENCES dbo.Seats(SeatId),
    CONSTRAINT CK_ShowSeats_Price CHECK (Price >= 0),
    CONSTRAINT CK_ShowSeats_Status CHECK (Status IN (N'Available', N'Held', N'Booked')),
    CONSTRAINT CK_ShowSeats_HoldFields CHECK
    (
        (Status = N'Held' AND HoldToken IS NOT NULL AND HoldExpiresAtUtc IS NOT NULL)
        OR (Status <> N'Held' AND HoldToken IS NULL AND HoldExpiresAtUtc IS NULL)
    ),
    CONSTRAINT CK_ShowSeats_BookingField CHECK
    (
        (Status = N'Booked' AND BookingId IS NOT NULL)
        OR (Status <> N'Booked' AND BookingId IS NULL)
    )
);
GO

CREATE INDEX IX_ShowSeats_Show_Status ON dbo.ShowSeats(ShowId, Status) INCLUDE (SeatId, Price);
CREATE INDEX IX_ShowSeats_ExpiredHolds ON dbo.ShowSeats(HoldExpiresAtUtc) WHERE Status = N'Held';
GO

CREATE TABLE dbo.Bookings
(
    BookingId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Bookings PRIMARY KEY,
    CustomerId      NVARCHAR(100) NOT NULL,
    ShowId          INT NOT NULL,
    Status          NVARCHAR(20) NOT NULL CONSTRAINT DF_Bookings_Status DEFAULT N'Pending',
    IdempotencyKey  NVARCHAR(100) NOT NULL,
    TotalAmount     DECIMAL(10,2) NOT NULL,
    Currency        CHAR(3) NOT NULL CONSTRAINT DF_Bookings_Currency DEFAULT 'USD',
    HoldExpiresAtUtc DATETIME2(3) NOT NULL,
    CreatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_Bookings_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_Bookings_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Bookings_Shows FOREIGN KEY (ShowId) REFERENCES dbo.Shows(ShowId),
    CONSTRAINT CK_Bookings_Status CHECK (Status IN (N'Pending', N'Confirmed', N'Expired', N'Cancelled', N'PaymentFailed')),
    CONSTRAINT CK_Bookings_TotalAmount CHECK (TotalAmount >= 0),
    CONSTRAINT UQ_Bookings_Customer_Idempotency UNIQUE (CustomerId, IdempotencyKey)
);
GO

/* Add this FK after Bookings exists, resolving the ShowSeats/Bookings reference. */
ALTER TABLE dbo.ShowSeats
    ADD CONSTRAINT FK_ShowSeats_Bookings FOREIGN KEY (BookingId) REFERENCES dbo.Bookings(BookingId);
GO

CREATE TABLE dbo.BookingSeats
(
    BookingId       UNIQUEIDENTIFIER NOT NULL,
    ShowId          INT NOT NULL,
    SeatId          INT NOT NULL,
    PriceAtBooking  DECIMAL(10,2) NOT NULL,
    CONSTRAINT PK_BookingSeats PRIMARY KEY (BookingId, SeatId),
    CONSTRAINT FK_BookingSeats_Bookings FOREIGN KEY (BookingId) REFERENCES dbo.Bookings(BookingId),
    CONSTRAINT FK_BookingSeats_ShowSeats FOREIGN KEY (ShowId, SeatId) REFERENCES dbo.ShowSeats(ShowId, SeatId),
    CONSTRAINT CK_BookingSeats_Price CHECK (PriceAtBooking >= 0)
);
GO

CREATE INDEX IX_BookingSeats_Show_Seat ON dbo.BookingSeats(ShowId, SeatId);
GO

CREATE TABLE dbo.Payments
(
    PaymentId       UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_Payments PRIMARY KEY,
    BookingId       UNIQUEIDENTIFIER NOT NULL,
    Provider        NVARCHAR(50) NOT NULL CONSTRAINT DF_Payments_Provider DEFAULT N'Simulation',
    ProviderRef     NVARCHAR(200) NULL,
    Amount          DECIMAL(10,2) NOT NULL,
    Currency        CHAR(3) NOT NULL CONSTRAINT DF_Payments_Currency DEFAULT 'USD',
    Status          NVARCHAR(20) NOT NULL CONSTRAINT DF_Payments_Status DEFAULT N'Pending',
    IdempotencyKey  NVARCHAR(100) NOT NULL,
    CreatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_Payments_CreatedAtUtc DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc    DATETIME2(3) NOT NULL CONSTRAINT DF_Payments_UpdatedAtUtc DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_Payments_Bookings FOREIGN KEY (BookingId) REFERENCES dbo.Bookings(BookingId),
    CONSTRAINT CK_Payments_Amount CHECK (Amount >= 0),
    CONSTRAINT CK_Payments_Status CHECK (Status IN (N'Pending', N'Succeeded', N'Failed', N'Refunded')),
    CONSTRAINT UQ_Payments_Booking_Idempotency UNIQUE (BookingId, IdempotencyKey)
);
GO

/* Outbox enables reliable publication of database changes to RabbitMQ. */
CREATE TABLE dbo.OutboxMessages
(
    OutboxMessageId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OutboxMessages PRIMARY KEY,
    EventType       NVARCHAR(200) NOT NULL,
    Payload         NVARCHAR(MAX) NOT NULL,
    OccurredAtUtc   DATETIME2(3) NOT NULL CONSTRAINT DF_OutboxMessages_OccurredAtUtc DEFAULT SYSUTCDATETIME(),
    ProcessedAtUtc  DATETIME2(3) NULL,
    RetryCount      INT NOT NULL CONSTRAINT DF_OutboxMessages_RetryCount DEFAULT 0,
    CONSTRAINT CK_OutboxMessages_RetryCount CHECK (RetryCount >= 0)
);
GO

CREATE INDEX IX_OutboxMessages_Unprocessed ON dbo.OutboxMessages(OccurredAtUtc) WHERE ProcessedAtUtc IS NULL;
GO
