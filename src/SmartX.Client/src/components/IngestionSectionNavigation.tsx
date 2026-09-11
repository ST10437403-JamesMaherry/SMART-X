/**
 * Permanent sections available inside the active Part 1 ingestion module.
 *
 * Native hash navigation keeps these controls lightweight because they move
 * between sections of the same React route rather than between application
 * pages.
 */
const ingestionSections = [
  {
    href: '#adaptive-anomaly-spotlight',
    label: 'Spotlight',
  },
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
 * Provides concise text-only navigation between the major Part 1 ingestion
 * workflows.
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
