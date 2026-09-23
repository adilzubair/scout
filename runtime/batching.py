"""Bounded batching for the pinned Laya 0.3.7 runtime; every state keeps its full budget."""
import math


def predict_many(agent, jobs, batch_size=8):
    if not jobs:
        return []
    # Contract fakes use the documented public interface.
    if not hasattr(agent, 'model'):
        return [agent.predict(state, {'answer': question})['answers']['answer'] for state, question in jobs]
    import torch
    from laya.common import build_sequence, collate_items, QTYPES, render_options, temp_bucket
    results = []
    for offset in range(0, len(jobs), batch_size):
        questions, items = [], []
        for state, question in jobs[offset:offset + batch_size]:
            agent._check_question('answer', question)
            q = agent._to_internal(question)
            seq, markers = build_sequence(agent.tok, state, q, agent.cfg.get('max_len', 512), agent.cfg.get('head_max_len', 192))
            if len(markers) != len(render_options(q)):
                raise ValueError('Laya option budget exceeded')
            questions.append(q)
            items.append({'ids': seq, 'markers': markers, 'qtype': QTYPES[q['t']]})
        batch = collate_items([items], agent.tok.pad_token_id)
        with torch.inference_mode(), torch.autocast(device_type=agent.device.type, dtype=agent.dtype, enabled=agent.device.type == 'cuda'):
            logits, _ = agent.model(batch['input_ids'].to(agent.device), batch['attention_mask'].to(agent.device), batch['marker_pos'].to(agent.device), batch['marker_mask'].to(agent.device), batch['qtype'].to(agent.device))
        logits = logits.float().cpu()
        for index, q in enumerate(questions):
            k = len(items[index]['markers'])
            temperature = agent.temperature_by_options.get(temp_bucket(QTYPES[q['t']], k), agent.temperature[QTYPES[q['t']]])
            if not math.isfinite(temperature) or temperature <= 0:
                raise ValueError('Invalid checkpoint temperature')
            probs = torch.softmax(logits[index, :k] / temperature, dim=-1).tolist()
            results.append({'probabilities': dict(zip(q['crit'], probs))})
    return results
