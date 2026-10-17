# Security

## Reporting a vulnerability

Report it privately through **Security → Report a vulnerability** on this repository. Do not open a public issue. You usually get an answer within seven days.

## Supported versions

The latest minor release receives security fixes. Older releases do not; upgrade by changing the image tag, as the README's Upgrading section describes.

## What Kairos holds

A Kairos instance stores invitee names and email addresses, meeting times, and integration credentials encrypted under `Security__SecretKey`. A finding that reaches any of them, or that lets someone other than the operator into `/admin`, is in scope.
