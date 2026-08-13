# TOOL_VIETSUB - Registration and email OTP checklist

## Foundation

- [x] REG-01 Define USER-only registration with the FREE plan.
- [x] REG-02 Define six-digit OTP, five-minute expiry, five attempts, and
  sixty-second resend cooldown.
- [x] REG-03 Deploy the idempotent V3 registration schema.
- [x] REG-04 Store Gmail SMTP credentials outside source control.

## Server

- [x] REG-05 Add cryptographic OTP generation and keyed OTP hashing.
- [x] REG-06 Add the SMTP email sender and branded HTML/plain-text templates.
- [x] REG-07 Implement `POST /api/v1/auth/register/start`.
- [x] REG-08 Implement `POST /api/v1/auth/register/verify`.
- [x] REG-09 Implement `POST /api/v1/auth/register/resend`.
- [x] REG-10 Add per-IP rate limits, per-email cooldown, attempt limits, and
  security audit events.
- [x] REG-11 Create USER + FREE subscription atomically and issue a session.
- [x] REG-12 Add expired-challenge cleanup and OpenAPI metadata.

## Desktop app

- [x] REG-13 Add native registration API contracts without exposing tokens to
  the WebView.
- [x] REG-14 Add strict start, verify, and resend WebView messages.
- [x] REG-15 Add automatic secure session storage after OTP verification.

## User interface

- [x] REG-16 Add Login/Register tabs to the authentication screen.
- [x] REG-17 Add display-name, email, password, and confirmation validation.
- [x] REG-18 Add accessible six-box OTP input with paste support.
- [x] REG-19 Add expiry/resend timers, busy states, and server errors.
- [x] REG-20 Visually validate the registration flow at supported sizes.

## Quality gate

- [x] REG-21 Test duplicate email, invalid OTP, expired OTP, attempt limits,
  resend cooldown, and device binding.
- [x] REG-22 Send a real Gmail SMTP delivery test without logging the OTP.
- [x] REG-23 Verify USER/FREE creation, auto-login, and registration cleanup.
- [x] REG-24 Build the frontend and full solution with zero warnings/errors.
- [x] REG-25 Audit NuGet/npm dependencies and confirm secrets are absent from
  tracked files.
