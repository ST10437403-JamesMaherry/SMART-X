import { maxAttachmentSizeBytes, type AttachmentCategory } from '../../api/attachmentsApi';

/**
 * Mirrors the backend attachment filename limit.
 */
const maxFileNameLength = 180;

/**
 * Category-specific file extensions supported by the Smart-X backend.
 *
 * These rules provide immediate client feedback while the backend remains the
 * authoritative security boundary.
 */
const categoryExtensions: Readonly<Record<AttachmentCategory, readonly string[]>> = {
  Configuration: ['.json', '.yaml', '.yml', '.cfg', '.txt'],
  DeploymentPhoto: ['.jpg', '.jpeg', '.png', '.webp'],
  HardwareLog: ['.log', '.txt', '.json', '.csv'],
};

/**
 * MIME types sent by the client for each supported extension.
 *
 * Browsers do not always report useful MIME types for configuration and log
 * files. Smart-X therefore maps known, already-validated extensions to a MIME
 * type accepted by the backend. Deployment photographs are still protected by
 * backend binary-signature inspection, so changing a file extension cannot
 * bypass actual image-format validation.
 */
const extensionContentTypes: Readonly<Record<string, string>> = {
  '.json': 'application/json',
  '.yaml': 'application/yaml',
  '.yml': 'application/yaml',
  '.cfg': 'text/plain',
  '.txt': 'text/plain',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.png': 'image/png',
  '.webp': 'image/webp',
  '.log': 'text/plain',
  '.csv': 'text/csv',
};

/**
 * Human-readable guidance kept deliberately concise for the minimal Smart-X
 * interface.
 */
const categoryGuidance: Readonly<Record<AttachmentCategory, string>> = {
  Configuration: 'JSON, YAML, CFG or TXT',
  DeploymentPhoto: 'JPG, PNG or WEBP',
  HardwareLog: 'LOG, TXT, JSON or CSV',
};

/**
 * Returns the lowercase extension from a client filename.
 */
function getFileExtension(fileName: string): string {
  const separatorIndex = fileName.lastIndexOf('.');

  if (separatorIndex < 0) {
    return '';
  }

  return fileName.slice(separatorIndex).toLowerCase();
}

/**
 * Detects C0 and C1 control characters without using a control-character
 * regular expression, keeping the project compatible with ESLint rules.
 */
function containsControlCharacter(value: string): boolean {
  return Array.from(value).some((character) => {
    const codePoint = character.codePointAt(0);

    if (codePoint === undefined) {
      return false;
    }

    const isC0Control = codePoint >= 0x00 && codePoint <= 0x1f;

    const isC1Control = codePoint >= 0x7f && codePoint <= 0x9f;

    return isC0Control || isC1Control;
  });
}

/**
 * Validates one selected attachment before an upload begins.
 *
 * Security-sensitive validation is repeated by the backend. Client validation
 * exists only to provide immediate, useful operator feedback.
 */
export function validateAttachmentFile(file: File, category: AttachmentCategory): string | null {
  const fileName = file.name.trim();

  if (!fileName) {
    return 'The selected file must have a name.';
  }

  if (fileName.length > maxFileNameLength) {
    return `The file name cannot exceed ${maxFileNameLength} characters.`;
  }

  if (containsControlCharacter(fileName)) {
    return 'The file name contains invalid control characters.';
  }

  if (fileName.includes('/') || fileName.includes('\\')) {
    return 'The file name cannot contain path separators.';
  }

  if (file.size === 0) {
    return 'Empty attachment files are not supported.';
  }

  if (file.size > maxAttachmentSizeBytes) {
    return 'The attachment cannot exceed 10 MiB.';
  }

  const extension = getFileExtension(fileName);

  if (!extension) {
    return 'The attachment must include a supported file extension.';
  }

  if (!categoryExtensions[category].includes(extension)) {
    return `${extension} files are not supported for ${formatAttachmentCategory(category)}.`;
  }

  return null;
}

/**
 * Returns an HTML file-picker accept value matched to the selected category.
 */
export function getAttachmentAccept(category: AttachmentCategory): string {
  return categoryExtensions[category].join(',');
}

/**
 * Returns concise supported-file guidance.
 */
export function getAttachmentGuidance(category: AttachmentCategory): string {
  return `${categoryGuidance[category]} · 10 MiB max`;
}

/**
 * Formats transport category names for display.
 */
export function formatAttachmentCategory(category: AttachmentCategory): string {
  if (category === 'DeploymentPhoto') {
    return 'Deployment photo';
  }

  if (category === 'HardwareLog') {
    return 'Hardware log';
  }

  return 'Configuration';
}

/**
 * Produces a File with a backend-supported content type.
 *
 * Some browsers report an empty or platform-specific MIME type for .log,
 * .cfg and .yaml files. The extension has already been validated, so assigning
 * the backend-supported media type makes multipart behaviour deterministic.
 *
 * The server does not trust this value for image authenticity: deployment
 * photographs are still inspected using binary signatures before encryption.
 */
export function prepareAttachmentFile(file: File): File {
  const extension = getFileExtension(file.name);

  const contentType = (extensionContentTypes[extension] ?? file.type) || 'application/octet-stream';

  return new File([file], file.name, {
    type: contentType,
    lastModified: file.lastModified,
  });
}
