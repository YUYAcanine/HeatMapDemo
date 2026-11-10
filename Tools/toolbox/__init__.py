"""Tools/ の実行ファイルが共通で使う部品。ここのファイルは直接実行しない.

  home_data_root.py … データの場所 (Assets/Data/HomeExperiment / D:\\HomeExperiment) を選ぶ
  export_data.py    … シーン5の書き出し(注視・距離・肩)を読む・到達の時刻・頭や肩の向きの計算
  report_html.py    … HTML レポートの色・表・折れ線・スタイルと CSV の書き出し
  arrival_model.py  … 注視可能性・到達前の視線履歴の特徴量・到達の予測モデル(リアルタイム用の OnlineArrivalPredictor も)
"""
