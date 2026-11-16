#!/bin/sh
# Zero-configuration, local-only attachment encryption key bootstrap.
#
# The key lives in its own persistent Docker volume, separate from encrypted
# attachment payloads. Never log or bake the key into an image.
set -eu
umask 077

key_file="/data/keys/attachment-key.base64"

# An explicit operator-provided key always takes precedence. This is useful
# for migration/testing, but changing keys will make old attachments unreadable.
if [ -z "${Attachments__EncryptionKeyBase64:-}" ]; then
  if [ -e "$key_file" ]; then
    if [ ! -f "$key_file" ] || [ ! -s "$key_file" ]; then
      echo "Smart-X: the stored attachment key is missing data or is not a file." >&2
      exit 1
    fi
  else
    # Parent directory is owned by the non-root .NET runtime account.
    # Create securely; a failed generation must not leave a valid-looking key.
    tmp_file="$(mktemp /data/keys/.attachment-key.XXXXXX)"
    trap 'rm -f "$tmp_file"' EXIT HUP INT TERM
    if ! openssl rand -base64 32 > "$tmp_file"; then
      echo "Smart-X: failed to generate a local attachment key." >&2
      exit 1
    fi
    chmod 600 "$tmp_file"
    mv "$tmp_file" "$key_file"
    trap - EXIT HUP INT TERM
  fi

  # Never print the key. Keep it in the API process environment only.
  Attachments__EncryptionKeyBase64="$(cat "$key_file")"
  export Attachments__EncryptionKeyBase64
fi


# Refuse malformed or missing keys immediately instead of failing only when a
# user eventually uploads a file. Never expose the decoded material to logs.
if ! printf '%s' "$Attachments__EncryptionKeyBase64" |
  openssl base64 -d -A > /dev/null 2>&1; then
  echo "Smart-X: the local attachment key is not valid Base64." >&2
  exit 1
fi
key_bytes="$(printf '%s' "$Attachments__EncryptionKeyBase64" |
  openssl base64 -d -A 2>/dev/null |
  wc -c | tr -d ' ')"
if [ "$key_bytes" != "32" ]; then
  echo "Smart-X: the attachment key must decode to exactly 32 bytes." >&2
  exit 1
fi

exec dotnet SmartX.Api.dll
