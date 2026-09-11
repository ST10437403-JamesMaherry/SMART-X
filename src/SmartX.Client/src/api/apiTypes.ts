/**
 * RFC-style problem response returned by the Smart-X API.
 *
 * The backend uses ASP.NET Core ProblemDetails for expected application,
 * domain and validation failures. Keeping that structure typed on the client
 * allows forms to display useful server feedback without depending on raw
 * response strings.
 */
export interface ApiProblemDetails {
  readonly type?: string;
  readonly title?: string;
  readonly status?: number;
  readonly detail?: string;
  readonly instance?: string;
  readonly traceId?: string;

  /**
   * ASP.NET validation responses may additionally expose one or more messages
   * grouped by the affected request field.
   */
  readonly errors?: Readonly<Record<string, readonly string[]>>;
}

/**
 * Represents a successful HTTP connection to the Smart-X API that returned a
 * non-success status code.
 *
 * Keeping HTTP status and ProblemDetails attached to the error allows feature
 * components to distinguish duplicate registration, validation failures and
 * missing resources without parsing message strings.
 */
export class ApiError extends Error {
  readonly status: number;
  readonly problem?: ApiProblemDetails;

  constructor(status: number, problem?: ApiProblemDetails) {
    super(problem?.detail ?? problem?.title ?? `The Smart-X API returned status ${status}.`);

    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
  }
}

/**
 * Converts an unknown failure into safe user-facing feedback.
 *
 * Expected backend ProblemDetails are preserved while network and unexpected
 * client failures use the supplied feature-specific fallback message.
 */
export function getApiErrorMessage(error: unknown, fallbackMessage: string): string {
  if (error instanceof ApiError) {
    return error.problem?.detail ?? error.problem?.title ?? fallbackMessage;
  }

  return fallbackMessage;
}
