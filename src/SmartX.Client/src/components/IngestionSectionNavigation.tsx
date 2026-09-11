/**
 * Permanent sections available inside the active Part 1 ingestion module.
 *
 * Native hash navigation keeps these controls lightweight and independent of
 * React Router because they move between sections of the same route rather
 * than between application pages.
 */
const ingestionSections = [
  {
    href: '#sensor-management',
    label: 'Sensors',
  },
  {
    href: '#telemetry-management',
    label: 'Telemetry',
  },
  {
    href: '#attachment-management',
    label: 'Attachments',
  },
] as const;

/**
 * Provides concise navigation between the major Part 1 ingestion workflows.
 *
 * The interface intentionally uses text-only controls with no icons or
 * decorative indicators so it remains consistent with the Smart-X visual
 * language.
 */
function IngestionSectionNavigation() {
  return (
    <nav className="ingestion-section-navigation" aria-label="Ingestion workspace sections">
      <ul className="ingestion-section-list">
        {ingestionSections.map((section) => (
          <li key={section.href}>
            <a href={section.href}>{section.label}</a>
          </li>
        ))}
      </ul>
    </nav>
  );
}

export default IngestionSectionNavigation;
