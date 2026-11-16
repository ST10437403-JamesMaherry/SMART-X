import { Navigate, Route, Routes } from 'react-router-dom';
import GatewayHomePage from './pages/GatewayHomePage';
import SimulationPage from './pages/SimulationPage';
import IngestionPage from './pages/IngestionPage';
import RealtimePage from './pages/RealtimePage';
import TopologyPage from './pages/TopologyPage';
import { routes } from './routing/routes';
import WorkspaceStateProvider from './state/WorkspaceStateProvider';
import './App.css';

/**
 * Declares the top-level Smart-X client routes.
 *
 * The provider stays mounted while route elements change, preserving useful
 * workspace state between all three architectural pillars.
 */
function App() {
  return (
    <WorkspaceStateProvider>
      <Routes>
        <Route path={routes.home} element={<GatewayHomePage />} />

        <Route path={routes.simulation} element={<SimulationPage />} />

        <Route path={routes.ingestion} element={<IngestionPage />} />

        <Route path={routes.realtime} element={<RealtimePage />} />

        <Route path={routes.topology} element={<TopologyPage />} />

        {/* Unknown client URLs recover safely instead of rendering a broken UI. */}
        <Route path="*" element={<Navigate to={routes.home} replace />} />
      </Routes>
    </WorkspaceStateProvider>
  );
}

export default App;
