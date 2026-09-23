import json
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).parent))
from ranking import fits, rank
from server import load_agent

class FakeAgent:
    def __init__(self, real):
        self.tok = real.tok
        self.cfg = real.cfg
        self._to_internal = real._to_internal
        self.calls = []
    def predict(self, state, questions):
        self.calls.append((state, questions))
        key = next(iter(questions))
        choices = list(questions[key]['criteria'])
        return {'answers': {key: {'probabilities': {c: float(i == 0) for i, c in enumerate(choices)}}}}

class ContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.real = load_agent('english', Path(__file__).resolve().parents[1] / '.runtime/models')
    def setUp(self):
        self.agent = FakeAgent(self.real)
    def test_budget_preserves_query(self):
        result = rank(self.agent, 'look ' * 200, [{'id': 'a', 'title': 'Report', 'kind': 'file'}])
        self.assertEqual({}, result['scores'])
        self.assertEqual([], self.agent.calls)
    def test_long_title_never_silently_truncated(self):
        result = rank(self.agent, 'report', [{'id': 'a', 'title': 'Different ' * 200, 'kind': 'file'}])
        self.assertEqual({}, result['scores'])
    def test_internal_ids_map_back_to_candidates(self):
        result = rank(self.agent, 'report', [{'id': 'local:1', 'title': 'Report', 'kind': 'file'}])
        self.assertEqual({'local:1': 1.0}, result['scores'])
    def test_binary_questions_use_neutral_choice_labels(self):
        result = rank(self.agent, 'reports', [{'id': 'a', 'title': 'Report A', 'kind': 'file'}, {'id': 'b', 'title': 'Report B', 'kind': 'file'}], True)
        self.assertEqual(['a', 'b'], result['members'])
        for _, questions in self.agent.calls:
            question = questions['answer']
            if question['instructions'].startswith('Does this item'):
                self.assertEqual({'A', 'B'}, set(question['criteria']))
                self.assertEqual('choice', question['type'])
    def test_no_partial_group_at_deadline(self):
        result = rank(self.agent, 'reports', [{'id': 'a', 'title': 'Report A', 'kind': 'file'}, {'id': 'b', 'title': 'Report B', 'kind': 'file'}], True, deadline_seconds=0)
        self.assertEqual([], result['members'])
    def test_workspace_cannot_be_group_member(self):
        result = rank(self.agent, 'reports', [{'id': 'a', 'title': 'Report A', 'kind': 'workspace'}, {'id': 'b', 'title': 'Report B', 'kind': 'workspace'}], True)
        self.assertEqual([], result['members'])

    def test_shortlist_covers_items_beyond_eight(self):
        items = [{'id': str(i), 'title': f'Quarterly document {i}.pdf', 'kind': 'file'} for i in range(32)]
        result = rank(self.agent, 'quarterly document', items)
        self.assertEqual(32, result['considered'])
        self.assertEqual(set(c['id'] for c in items), set(result['scores']))
        self.assertAlmostEqual(1, sum(result['scores'].values()) + result['none'])

    def test_long_first_candidate_does_not_hide_later_items(self):
        result = rank(self.agent, 'report', [{'id': 'long', 'title': 'word ' * 400, 'kind': 'file'}, {'id': 'report', 'title': 'Report.pdf', 'kind': 'file'}])
        self.assertIn('report', result['scores'])
        self.assertNotIn('long', result['scores'])

    def test_missing_distribution_entries_are_rejected(self):
        from ranking import distribution
        with self.assertRaises(ValueError):
            distribution({'probabilities': {'a': 1.0}}, {'criteria': {'a': 'Yes', 'b': 'No'}})

    def test_batch_matches_public_runtime(self):
        from batching import predict_many
        question = {'type': 'choice', 'instructions': 'Which app is named?', 'criteria': {'a': 'Notepad', 'b': 'Calculator'}}
        expected = self.real.predict('notepad', {'target': question})['answers']['target']['probabilities']
        actual = predict_many(self.real, [('notepad', question)])[0]['probabilities']
        for key in expected:
            self.assertAlmostEqual(expected[key], actual[key], places=3)

if __name__ == '__main__':
    unittest.main()
