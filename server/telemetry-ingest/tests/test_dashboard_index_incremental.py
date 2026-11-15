"""Component-cache and bounded-I/O regressions for the background indexer."""

import json
import sqlite3
import unittest
import weakref
import zlib
from contextlib import contextmanager
from unittest.mock import patch

from app import dashboard_index
import test_dashboard_index as fixtures


class DashboardIndexIncrementalTests(unittest.TestCase):
    # Use the existing public archive/source fixture without collecting its
    # entire test class a second time under unittest discovery.
    setUp = fixtures.DashboardIndexTests.setUp
    tearDown = fixtures.DashboardIndexTests.tearDown
    archive = fixtures.DashboardIndexTests.archive

    @staticmethod
    def record(identifier='flight'):
        return {'summary': {'Id': identifier, 'ContactNumber': 1},
                'series': {'t': [-.5, 0], 'agl': [20, 5],
                           'ground': [False, True], 'defElev': [25, 25]}}

    def restart(self):
        self.index.stop()
        self.index = dashboard_index.DashboardIndex(self.root, self.cache)
        self.index._initialize_cache()

    def cache_rows(self):
        with self.index._connect_cache() as connection:
            summaries = [tuple(row) for row in connection.execute(
                'SELECT * FROM archive_index ORDER BY sha256')]
            analytics = [tuple(row) for row in connection.execute(
                'SELECT * FROM flight_analytics ORDER BY sha256')]
        return summaries, analytics

    def test_unchanged_refresh_reads_headers_without_redecoding_or_republishing_catalog(self):
        self.archive(records=[self.record()])
        self.index._refresh()
        self.assertEqual(1, self.index.analytics_catalog()['total'])
        catalog = self.index._catalog_snapshot
        reads = []
        connect = self.index._connect_cache

        def authorize(action, table, column, database, trigger):
            if action == sqlite3.SQLITE_READ:
                reads.append((table, column))
                if table in ('archive_index', 'flight_analytics') and column in ('payload', 'compact'):
                    return sqlite3.SQLITE_DENY
            return sqlite3.SQLITE_OK

        @contextmanager
        def headers_only():
            with connect() as connection:
                connection.set_authorizer(authorize)
                yield connection

        with patch.object(self.index, '_connect_cache', headers_only), \
                patch('app.dashboard_index._index_archive', side_effect=AssertionError('unchanged archive reopened')), \
                patch('app.dashboard_index.json.loads', side_effect=AssertionError('unchanged cache decoded again')):
            self.index._refresh()
            self.index._refresh()
        self.assertEqual(1, self.index.summary()['index']['indexed'])
        self.assertEqual(1, self.index.analytics_catalog()['total'])
        self.assertIs(catalog, self.index._catalog_snapshot)
        self.assertFalse(any(column in ('payload', 'compact') for _, column in reads))

    def test_metadata_query_uses_covering_storage_without_large_detail_rows(self):
        with self.index._connect_cache() as connection:
            plan = connection.execute('EXPLAIN QUERY PLAN SELECT sha256,version,indexed_at,error FROM archive_index').fetchall()
        self.assertTrue(any('COVERING INDEX' in row['detail'].upper() for row in plan), plan)

    def test_retention_releases_decoded_data_and_all_component_cache_rows(self):
        self.archive(records=[self.record()])
        self.index._refresh()
        self.assertEqual(1, self.index.analytics_catalog()['total'])
        self.db.execute('DELETE FROM captures')
        self.db.commit()
        self.index._refresh()
        self.assertEqual(0, self.index.analytics_catalog()['total'])
        self.assertEqual({}, self.index._decoded_cache)
        with self.index._connect_cache() as connection:
            for table in ('archive_index', 'flight_analytics', 'analytics_revision'):
                self.assertEqual(0, connection.execute(f'SELECT COUNT(*) FROM {table}').fetchone()[0], table)

    def test_new_upload_does_not_decode_or_rebuild_unchanged_cached_archives(self):
        self.archive('old', records=[self.record('old')], offset=-10)
        self.index._refresh()
        with self.index._connect_cache() as connection:
            old_compact = connection.execute('SELECT compact FROM archive_index').fetchone()[0]
        self.archive('new', records=[self.record('new')])
        decode = json.loads
        seen = []

        def checked_decode(value, *args, **kwargs):
            self.assertNotEqual(old_compact, value, 'unchanged compact JSON must be reused')
            return decode(value, *args, **kwargs)

        # Capture the real function outside the patch; unrelated archives must
        # keep their already decoded summaries while the new archive is built.
        original = dashboard_index._index_archive
        def index_new_only(path, row, **kwargs):
            seen.append(row['capture_id'])
            return original(path, row, **kwargs)

        with patch('app.dashboard_index._index_archive', side_effect=index_new_only), \
                patch('app.dashboard_index.json.loads', side_effect=checked_decode):
            self.index._refresh()
        self.assertEqual(['new'], seen)
        self.assertEqual(2, self.index.analytics_catalog()['total'])

    def test_restart_and_repeated_initialization_preserve_current_cached_data(self):
        path = self.archive(records=[self.record()])
        original = path.read_bytes()
        source = self.db.execute('SELECT * FROM captures').fetchall()
        self.index._refresh()
        before = self.cache_rows()
        self.restart()
        with patch('app.dashboard_index._index_archive', side_effect=AssertionError('deployment invalidated valid cache')):
            self.index._initialize_cache()
            self.index._refresh()
        self.assertEqual(before, self.cache_rows())
        self.assertEqual(1, self.index.analytics_catalog()['total'])
        self.assertEqual(25, self.index.analytics_landing('flight')['profiles']['t']['elev'][300])
        self.assertEqual(original, path.read_bytes())
        self.assertEqual(source, self.db.execute('SELECT * FROM captures').fetchall())

    def test_legacy_current_cache_migration_preserves_payloads_and_analytics(self):
        self.archive(records=[self.record()])
        self.index._refresh()
        before = self.cache_rows()
        with self.index._connect_cache() as connection:
            connection.execute('DROP TABLE analytics_revision')
            connection.execute('DROP INDEX archive_index_metadata')
        self.restart()
        self.assertEqual(before, self.cache_rows())
        with self.index._connect_cache() as connection:
            revisions = [row[0] for row in connection.execute('SELECT version FROM analytics_revision')]
        self.assertEqual([dashboard_index.ANALYTICS_VERSION], revisions)
        with patch('app.dashboard_index._index_archive', side_effect=AssertionError('migration must reuse valid components')):
            self.index._refresh()
        self.assertEqual(before, self.cache_rows())
        self.assertEqual(1, self.index.analytics_catalog()['total'])

    def test_summary_revision_upgrade_preserves_valid_analytics_byte_for_byte(self):
        self.archive(records=[self.record()])
        self.index._refresh()
        with self.index._connect_cache() as connection:
            cached = connection.execute('SELECT payload,compact FROM archive_index').fetchone()
            payload, compact = json.loads(cached['payload']), json.loads(cached['compact'])
            for value in (payload, compact):
                value['telemetry']['nonfinite_values'] = 7
                value['warnings'].append('nonfinite_values: 7')
            connection.execute('UPDATE archive_index SET version=13,payload=?,compact=?',
                               (json.dumps(payload), json.dumps(compact)))
            connection.execute('DROP TABLE analytics_revision')
        before_analytics = self.cache_rows()[1]
        self.restart()
        with patch('app.dashboard_index.build_analytics', side_effect=AssertionError('valid analytics were recomputed')):
            self.index._refresh()
        self.assertEqual(before_analytics, self.cache_rows()[1])
        self.assertEqual(1, self.index.summary()['index']['indexed'])
        detail = self.index.capture('one')
        self.assertEqual(0, detail['telemetry']['nonfinite_values'])
        self.assertNotIn('nonfinite_values: 7', detail['warnings'])
        with self.index._connect_cache() as connection:
            self.assertEqual(dashboard_index.INDEX_VERSION, connection.execute('SELECT version FROM archive_index').fetchone()[0])
        self.assertEqual(25, self.index.analytics_landing('flight')['profiles']['t']['elev'][300])

    def test_analytics_revision_upgrade_reuses_valid_summary_without_telemetry_scan(self):
        self.archive(records=[self.record()])
        self.index._refresh()
        summary_before = self.index.capture('one')
        with self.index._connect_cache() as connection:
            cached = connection.execute('SELECT sha256,payload FROM flight_analytics').fetchone()
            analytics = json.loads(zlib.decompress(cached['payload']))
            analytics[0]['profiles']['t']['elev'][300] = 2500
            connection.execute('UPDATE flight_analytics SET payload=? WHERE sha256=?',
                               (zlib.compress(json.dumps(analytics).encode()), cached['sha256']))
            connection.execute('UPDATE analytics_revision SET version=12 WHERE sha256=?', (cached['sha256'],))
        self.restart()
        with patch('app.dashboard_index._telemetry', side_effect=AssertionError('valid summary was recomputed')):
            self.index._refresh()
        self.assertEqual(1, self.index.summary()['index']['indexed'])
        self.assertEqual(25, self.index.analytics_landing('flight')['profiles']['t']['elev'][300])
        summary_after = self.index.capture('one')
        for field in ('telemetry', 'journal', 'session', 'landings', 'warnings'):
            self.assertEqual(summary_before[field], summary_after[field], field)
        with self.index._connect_cache() as connection:
            self.assertEqual(dashboard_index.ANALYTICS_VERSION,
                             connection.execute('SELECT version FROM analytics_revision').fetchone()[0])

    def legacy_cache(self, version, *, capture='one', counter=0, invalid_shape=False):
        self.archive(capture, records=[self.record(capture)])
        self.index._refresh()
        digest = self.db.execute('SELECT sha256 FROM captures WHERE capture_id=?', (capture,)).fetchone()[0]
        with self.index._connect_cache() as connection:
            stored = connection.execute('SELECT payload,compact FROM archive_index WHERE sha256=?', (digest,)).fetchone()
            payload, compact = json.loads(stored['payload']), json.loads(stored['compact'])
            for value in (payload, compact):
                if counter == 'absent':
                    value['telemetry'].pop('nonfinite_values')
                else:
                    value['telemetry']['nonfinite_values'] = counter
            if invalid_shape:
                payload['journal'].pop('entries')
            connection.execute('UPDATE archive_index SET version=?,payload=?,compact=? WHERE sha256=?',
                               (version, json.dumps(payload), json.dumps(compact), digest))
            if version < dashboard_index.ANALYTICS_VERSION:
                stored = connection.execute('SELECT payload FROM flight_analytics WHERE sha256=?', (digest,)).fetchone()
                stale = json.loads(zlib.decompress(stored['payload']))
                stale[0]['profiles']['t']['elev'][300] = 2500
                connection.execute('UPDATE flight_analytics SET payload=? WHERE sha256=?',
                                   (zlib.compress(json.dumps(stale).encode()), digest))
            connection.execute('DROP TABLE analytics_revision')
        self.restart()

    def test_version_eight_zero_nonfinite_summary_is_reused_while_stale_analytics_rebuild(self):
        self.legacy_cache(8)
        with patch('app.dashboard_index._telemetry', side_effect=AssertionError('compatible summary rescanned')), \
                patch('app.dashboard_index.build_analytics', wraps=dashboard_index.build_analytics) as build:
            self.index._refresh()
        build.assert_called_once()
        self.assertEqual(1, self.index.summary()['index']['indexed'])
        self.assertEqual(25, self.index.analytics_landing('one')['profiles']['t']['elev'][300])
        with self.index._connect_cache() as connection:
            self.assertEqual(dashboard_index.INDEX_VERSION,
                             connection.execute('SELECT version FROM archive_index').fetchone()[0])
            self.assertEqual(dashboard_index.ANALYTICS_VERSION,
                             connection.execute('SELECT version FROM analytics_revision').fetchone()[0])

    def test_version_thirteen_zero_nonfinite_keeps_both_compatible_components(self):
        self.legacy_cache(13)
        analytics_before = self.cache_rows()[1]
        with patch('app.dashboard_index._telemetry', side_effect=AssertionError('compatible summary rescanned')), \
                patch('app.dashboard_index.build_analytics', side_effect=AssertionError('compatible analytics rebuilt')):
            self.index._refresh()
        self.assertEqual(1, self.index.summary()['index']['indexed'])
        self.assertEqual(analytics_before, self.cache_rows()[1])
        self.assertEqual(25, self.index.analytics_landing('one')['profiles']['t']['elev'][300])

    def test_legacy_nonzero_missing_or_invalid_nonfinite_count_requires_full_summary(self):
        for index, counter in enumerate((7, 'absent', None, False, '0')):
            with self.subTest(counter=counter):
                self.legacy_cache(8, capture=f'counter-{index}', counter=counter)
                with patch('app.dashboard_index._telemetry', wraps=dashboard_index._telemetry) as telemetry:
                    self.index._refresh()
                telemetry.assert_called_once()
                self.assertEqual(0, self.index.capture(f'counter-{index}')['telemetry']['nonfinite_values'])
                self.assertEqual(25, self.index.analytics_landing(f'counter-{index}')['profiles']['t']['elev'][300])

    def test_unproven_summary_revision_or_invalid_payload_shape_is_rebuilt(self):
        for version, invalid_shape in ((12, False), (8, True)):
            capture = f'version-{version}'
            with self.subTest(version=version, invalid_shape=invalid_shape):
                self.legacy_cache(version, capture=capture, invalid_shape=invalid_shape)
                with patch('app.dashboard_index._telemetry', wraps=dashboard_index._telemetry) as telemetry:
                    self.index._refresh()
                telemetry.assert_called_once()
                self.assertEqual(0, self.index.summary()['index']['errors'])
                self.assertEqual(25, self.index.analytics_landing(capture)['profiles']['t']['elev'][300])

    def test_legacy_summary_compatibility_does_not_extend_to_future_format_changes(self):
        self.legacy_cache(8)
        with patch('app.dashboard_index.INDEX_VERSION', 15), \
                patch('app.dashboard_index._telemetry', wraps=dashboard_index._telemetry) as telemetry:
            self.index._refresh()
        telemetry.assert_called_once()
        self.assertEqual(0, self.index.summary()['index']['errors'])

    def test_previous_archive_working_data_is_released_before_next_rebuild(self):
        class Journal(dict):
            pass

        class Analytics(list):
            pass

        self.archive('first', records=[self.record('first')], offset=-10)
        self.archive('second', records=[self.record('second')])
        references = []
        original = dashboard_index._index_archive

        def track(*args, **kwargs):
            self.assertTrue(all(reference() is None for reference in references),
                            'previous archive working data overlaps the next rebuild')
            payload = original(*args, **kwargs)
            payload['journal'] = Journal(payload['journal'])
            payload['analytics'] = Analytics(payload['analytics'])
            references.extend((weakref.ref(payload['journal']), weakref.ref(payload['analytics'])))
            return payload

        with patch('app.dashboard_index._index_archive', side_effect=track) as build, \
                patch('app.dashboard_index.BACKFILL_SECONDS', float('inf')):
            self.index._refresh()
        self.assertEqual(2, build.call_count)
        self.assertEqual(2, self.index.summary()['index']['indexed'])
        self.assertTrue(all(reference() is None for reference in references))

    def test_pending_backfill_continues_without_periodic_scan_sleep(self):
        index = self.index
        pending = iter((2, 1, 0))
        refreshes, waits = [], []

        class StopAfterIdle:
            stopped = False

            def is_set(self):
                return self.stopped

            def set(self):
                self.stopped = True

            def wait(self, timeout):
                waits.append((index._state['pending'], timeout))
                # End even if the implementation incorrectly idles during
                # backfill; that produces a bounded, informative test failure.
                if timeout >= dashboard_index.SCAN_SECONDS:
                    self.stopped = True
                return self.stopped

        def refresh():
            value = next(pending)
            refreshes.append(value)
            index._state.update(state='indexing' if value else 'ready', pending=value)

        index._stop = StopAfterIdle()
        with patch.object(index, '_initialize_cache'), patch.object(index, '_refresh', side_effect=refresh):
            index._run()
        self.assertEqual([2, 1, 0], refreshes)
        self.assertEqual([(0, dashboard_index.SCAN_SECONDS)], waits)


if __name__ == '__main__':
    unittest.main()
